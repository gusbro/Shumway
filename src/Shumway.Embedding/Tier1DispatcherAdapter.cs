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

    // The activation this query runs on: a dynamic predicate's snapshot is
    // linked into its code space when it promotes (ADR-054).
    private readonly Activation? _engine;

    public Tier1DispatcherAdapter(
        IlPromotionStore store,
        IReadOnlyDictionary<int, CompiledPredicate> predicatesByAddress,
        JitIndexProfile jitProfile,
        Activation? engine = null)
    {
        _store = store;
        _predicatesByAddress = predicatesByAddress;
        _jitProfile = jitProfile;
        _engine = engine;
    }

    /// <summary>A debug evaluation's query runs its wasm-covered predicates
    /// on bytecode and promotes none to wasm (IL is unaffected: ADR-035's
    /// per-predicate rule stands). The browser evaluates on a pool thread
    /// while the stop holds the engine thread, and entering or promoting a
    /// wasm module there waits on that thread until the evaluation times
    /// out.</summary>
    public bool WasmSuspended { get; init; }

    private bool SuspendsWasm(int functorId)
        => WasmSuspended && _store.Wasm is { } wasm && wasm.Covers(functorId);

    public Func<Activation, int, bool>? ResolveRetiredResume(int functorId)
        => _store.TryGetRetiredResumeWrapper(functorId);

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
        => SuspendsWasm(functorId) ? null : _store.TryGetResumeWrapper(functorId);

    // Built on the first miss only: a CallIl site losing its delegate is an
    // eviction between queries, not a per-dispatch event.
    private Dictionary<int, int>? _addressByFid;

    public bool IsPredicateEntry(int address) => _predicatesByAddress.ContainsKey(address);

    // Call site -> the predicate whose code holds it (null: none), filled on
    // first use; the predicates' spans, sorted by address, for the lookup.
    private readonly Dictionary<int, CompiledPredicate?> _callerBySite = new();
    private int[]? _spanStarts;
    private CompiledPredicate[]? _spanPreds;

    public void CreditCaller(int sitePc)
    {
        if (!_callerBySite.TryGetValue(sitePc, out var caller))
            _callerBySite[sitePc] = caller = PredicateContaining(sitePc);
        if (caller is null) return;
        int functorId = caller.FunctorId;
        if (_store.TryGet(functorId) is not null || _store.IsUnpromotable(functorId)) return;
        if (SuspendsWasm(functorId)) return;
        // Counted as a dispatch of the caller, the way OnDispatch counts one:
        // the wasm tier first, then IL.
        if (!WasmSuspended && _store.Wasm is { Enabled: true } wasm)
        {
            int addr = AddressOfFunctor(functorId);
            if (addr >= 0) wasm.RecordDispatch(functorId, caller, addr, _engine);
            return;
        }
        _store.RecordInvocation(functorId, caller, CalleeMap);
    }

    private CompiledPredicate? PredicateContaining(int pc)
    {
        if (_spanStarts is null)
        {
            var starts = new List<int>(_predicatesByAddress.Count);
            foreach (var (addr, _) in _predicatesByAddress) starts.Add(addr);
            starts.Sort();
            _spanStarts = starts.ToArray();
            _spanPreds = new CompiledPredicate[_spanStarts.Length];
            for (int i = 0; i < _spanStarts.Length; i++)
                _spanPreds[i] = _predicatesByAddress[_spanStarts[i]];
        }
        int idx = System.Array.BinarySearch(_spanStarts, pc);
        if (idx < 0) idx = ~idx - 1;
        if (idx < 0) return null;
        var pred = _spanPreds![idx];
        return pc < _spanStarts[idx] + pred.Bytecode.Length ? pred : null;
    }

    public int AddressOfFunctor(int functorId)
    {
        if (_addressByFid is null)
        {
            _addressByFid = new Dictionary<int, int>(_predicatesByAddress.Count);
            foreach (var (addr, pred) in _predicatesByAddress)
                _addressByFid[pred.FunctorId] = addr;
        }
        return _addressByFid.TryGetValue(functorId, out int a) ? a : -1;
    }

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

        if (SuspendsWasm(functorId))
        {
            _dispatchCache[targetAddress] = null!;
            return null;
        }

        var existing = _store.TryGetDispatchWrapper(functorId);
        if (existing is not null)
        {
            _dispatchCache[targetAddress] = existing;
            return existing;
        }

        // The wasm tier counts the same dispatches; its install lands in the
        // store's table, so the wrapper below is the ordinary one.
        if (!WasmSuspended && _store.Wasm is { Enabled: true } wasm
            && wasm.RecordDispatch(functorId, pred, targetAddress, _engine) is not null)
        {
            var wrappedWasm = _store.TryGetDispatchWrapper(functorId)!;
            _dispatchCache[targetAddress] = wrappedWasm;
            return wrappedWasm;
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
