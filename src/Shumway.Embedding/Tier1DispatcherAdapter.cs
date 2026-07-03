using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Bridges the bytecode interpreter's address-keyed <see cref="ITier1Dispatcher"/>
/// contract to the engine's functor-keyed <see cref="IlPromotionStore"/>. The
/// adapter holds the per-query <c>address → CompiledPredicate</c> map produced
/// by <see cref="Linker"/> so the interpreter can hand off the target bytecode
/// address and get back an IL delegate (or <c>null</c>) without ever learning
/// what the predicate's name was.
///
/// <para>One adapter is built per <see cref="PrologEngine.SetupQueryFromTerm"/>
/// invocation. The address map is per-query (addresses change every time the
/// program is re-linked), but the underlying <see cref="IlPromotionStore"/>
/// lives on the engine and survives across queries — so promotion state
/// (counts, compiled delegates) accumulates over time.</para>
///
/// <para>Phase 33 B3 — the dispatch/resume wrapper closures live on the STORE
/// (engine lifetime, invalidated on delegate install/evict), so this per-query
/// adapter allocates none; and the functor-keyed callee map is built lazily on
/// the first dispatch that actually needs it (a compile decision), so a
/// steady-state query over already-promoted / already-rejected predicates pays
/// nothing for it at setup.</para>
/// </summary>
internal sealed class Tier1DispatcherAdapter : ITier1Dispatcher
{
    private readonly IlPromotionStore _store;
    private readonly IReadOnlyDictionary<int, CompiledPredicate> _predicatesByAddress;
    private Dictionary<int, CompiledPredicate>? _calleeMap;
    private readonly JitIndexProfile _jitProfile;

    // Phase 18 chunk 202 — the bytecode interpreter's
    // DispatchToTier1OrBytecode calls OnDispatch(targetAddress) for every
    // Call / Execute, so a resolved answer (including null for "nothing to
    // promote here") is cached per address: the fast-loop is a single
    // dictionary probe. The values are the store's engine-lifetime wrappers
    // (B3), not per-query closures.
    private readonly Dictionary<int, Func<Engine, bool>> _dispatchCache = new();

    public Tier1DispatcherAdapter(
        IlPromotionStore store,
        IReadOnlyDictionary<int, CompiledPredicate> predicatesByAddress,
        JitIndexProfile jitProfile)
    {
        _store = store;
        _predicatesByAddress = predicatesByAddress;
        _jitProfile = jitProfile;
    }

    // Chunk 50 — a functor-id-keyed view so IL CanCompile can inspect callees
    // by id. predicatesByAddress is keyed by bytecode address; the same
    // predicate appears under each of its addresses, but the functor id is
    // unique. Built on first use (B3): only a dispatch that reaches a compile
    // decision needs it.
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

    public Func<Engine, int, bool>? ResolveByFunctorId(int functorId)
        // Phase 16 — threaded resume. Returns the store's engine-lifetime
        // cached wrapper over the bound IL delegate (or null if the predicate
        // isn't promoted, which shouldn't happen for a marker we ourselves
        // emitted but we defend anyway).
        => _store.TryGetResumeWrapper(functorId);

    public Func<Engine, bool>? OnDispatch(int targetAddress)
    {
        // Phase 18 chunk 202 hot path: a previously-resolved cache
        // entry skips every lookup below.
        if (_dispatchCache.TryGetValue(targetAddress, out var cached)) return cached;

        // Fast path: address has no associated predicate (it's a launcher
        // stub or an unindexed clause body). Nothing to promote.
        if (!_predicatesByAddress.TryGetValue(targetAddress, out var pred))
        {
            _dispatchCache[targetAddress] = null!;
            return null;
        }

        int functorId = pred.FunctorId;

        // Already promoted? Cache the store's wrapper per address, return.
        var existing = _store.TryGetDispatchWrapper(functorId);
        if (existing is not null)
        {
            _dispatchCache[targetAddress] = existing;
            return existing;
        }

        // Chunk 75 — JIT indexing profile. The dynamic-predicate
        // recompile threshold cares ONLY about not-yet-promoted
        // predicates (a promoted predicate already runs as IL — the
        // indexing decision is moot). Bumping the counter on every
        // call to a hot promoted predicate added a dictionary
        // lookup + write to the per-call cost for no benefit.
        _jitProfile.RecordCall(functorId);

        // Fast path for already-rejected predicates. Without this,
        // every call to a dynamic / oversized / layout-excluded
        // predicate (the majority of dispatches in a real program)
        // pays the full RecordInvocation entry sequence (3-5 dict
        // ops) on top of the bytecode dispatch — a 7× slowdown over
        // Tier-0 on Blint. The store's _unpromotable set is the
        // exact answer to "is this functor a wasted RecordInvocation
        // call?" so check it directly and bail.
        if (_store.IsUnpromotable(functorId))
        {
            _dispatchCache[targetAddress] = null!;
            return null;
        }

        // Otherwise let the store decide whether the counter has crossed
        // the threshold and a compile should fire now. Hand the
        // callee map through so IL Call eligibility can be evaluated.
        // We DON'T cache the null result here — the next call may
        // cross the threshold and promote. Once promoted, the
        // existing-branch above caches.
        var fresh = _store.RecordInvocation(functorId, pred, CalleeMap);
        if (fresh is null) return null;
        var wrappedFresh = _store.TryGetDispatchWrapper(functorId)!;
        _dispatchCache[targetAddress] = wrappedFresh;
        return wrappedFresh;
    }
}
