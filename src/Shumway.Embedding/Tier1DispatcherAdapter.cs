using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Bridges the interpreter's address-keyed <see cref="ITier1Dispatcher"/> to the
/// engine's functor-keyed <see cref="IlPromotionStore"/>. One adapter per query
/// setup: the address map is per-query (re-linked every setup), the store and its
/// promotion state are engine-lifetime.
/// </summary>
internal sealed class Tier1DispatcherAdapter : ITier1Dispatcher
{
    private readonly IlPromotionStore _store;
    private readonly IReadOnlyDictionary<int, CompiledPredicate> _predicatesByAddress;
    private Dictionary<int, CompiledPredicate>? _calleeMap;
    private readonly JitIndexProfile _jitProfile;

    // Per-address answer cache (including null for "nothing to promote here") so the
    // per-Call hot path is a single dictionary probe. Values are the store's
    // engine-lifetime wrappers, not per-query closures.
    private readonly Dictionary<int, Func<Activation, bool>> _dispatchCache = new();

    public Tier1DispatcherAdapter(
        IlPromotionStore store,
        IReadOnlyDictionary<int, CompiledPredicate> predicatesByAddress,
        JitIndexProfile jitProfile)
    {
        _store = store;
        _predicatesByAddress = predicatesByAddress;
        _jitProfile = jitProfile;
    }

    // Functor-keyed view for IL CanCompile's callee inspection. Built lazily: only a
    // dispatch that reaches a compile decision needs it.
    private Dictionary<int, CompiledPredicate> CalleeMap
    {
        get
        {
            if (_calleeMap is null)
            {
                _calleeMap = new Dictionary<int, CompiledPredicate>(_predicatesByAddress.Count);
                foreach (var (_, pred) in _predicatesByAddress)
                    _calleeMap[pred.FunctorId] = pred;
            }
            return _calleeMap;
        }
    }

    public Func<Activation, int, bool>? ResolveByFunctorId(int functorId)
        => _store.TryGetResumeWrapper(functorId);

    // The store's eviction stamp this cache was built against. Eviction cannot reach
    // wrappers already cached here by address, and a stale wrapper serving an evicted
    // dynamic snapshot violates the logical update view — so one int compare per
    // dispatch, cache dropped wholesale when the stamp moved (evictions are rare).
    private int _evictionStampSeen;

    public Func<Activation, bool>? OnDispatch(int targetAddress)
    {
        if (_evictionStampSeen != _store.EvictionStamp)
        {
            _dispatchCache.Clear();
            _evictionStampSeen = _store.EvictionStamp;
        }
        if (_dispatchCache.TryGetValue(targetAddress, out var cached)) return cached;

        // No predicate at this address (launcher stub, unindexed clause body).
        if (!_predicatesByAddress.TryGetValue(targetAddress, out var pred))
        {
            _dispatchCache[targetAddress] = null!;
            return null;
        }

        int functorId = pred.FunctorId;

        var existing = _store.TryGetDispatchWrapper(functorId);
        if (existing is not null)
        {
            _dispatchCache[targetAddress] = existing;
            return existing;
        }

        // JIT-indexing profile counts only not-yet-promoted predicates — a promoted
        // one already runs as IL, so the indexing decision is moot.
        _jitProfile.RecordCall(functorId);

        // Already-rejected predicates (dynamic / oversized / layout-excluded) are the
        // majority of dispatches in a real program; without this early-out each would
        // pay RecordInvocation's full entry sequence on every call.
        if (_store.IsUnpromotable(functorId))
        {
            _dispatchCache[targetAddress] = null!;
            return null;
        }

        // Not cached when null: the next call may cross the promotion threshold.
        var fresh = _store.RecordInvocation(functorId, pred, CalleeMap);
        if (fresh is null) return null;
        var wrappedFresh = _store.TryGetDispatchWrapper(functorId)!;
        _dispatchCache[targetAddress] = wrappedFresh;
        return wrappedFresh;
    }
}
