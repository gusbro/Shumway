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
/// </summary>
internal sealed class Tier1DispatcherAdapter : ITier1Dispatcher
{
    private readonly IlPromotionStore _store;
    private readonly IReadOnlyDictionary<int, CompiledPredicate> _predicatesByAddress;
    private readonly Dictionary<int, CompiledPredicate> _calleeMap;
    private readonly JitIndexProfile _jitProfile;
    // Phase 16 chunk 182 — cache the Func<Engine,int,bool> adapter per
    // functor id. The bytecode interpreter's resume-marker dispatch
    // fires *per Call return* (millions of times in a real program);
    // wrapping the PredicateDelegate in a fresh closure on each
    // lookup is GC pressure proportional to call frequency. The
    // cached wrapper lives as long as the dispatcher does — which
    // is one query setup, the same lifetime as the IlPromotion store
    // entries it points into.
    private readonly Dictionary<int, Func<Engine, int, bool>> _resumeCache = new();

    public Tier1DispatcherAdapter(
        IlPromotionStore store,
        IReadOnlyDictionary<int, CompiledPredicate> predicatesByAddress,
        JitIndexProfile jitProfile)
    {
        _store = store;
        _predicatesByAddress = predicatesByAddress;
        _jitProfile = jitProfile;
        // Build a functor-id-keyed view so IL CanCompile can inspect
        // callees by id (chunk 50). predicatesByAddress is keyed by
        // bytecode address; the same predicate appears under each of
        // its addresses, but the functor id is unique.
        _calleeMap = new Dictionary<int, CompiledPredicate>(predicatesByAddress.Count);
        foreach (var (_, pred) in predicatesByAddress)
            _calleeMap[pred.FunctorId] = pred;
    }

    public Func<Engine, int, bool>? ResolveByFunctorId(int functorId)
    {
        // Phase 16 — threaded resume. Returns the already-bound IL
        // delegate (or null if the predicate isn't promoted yet,
        // which shouldn't happen for a marker we ourselves emitted
        // but we defend anyway). The wrapper is cached so resume-
        // marker dispatch doesn't allocate a fresh closure on every
        // Call return.
        if (_resumeCache.TryGetValue(functorId, out var cached))
            return cached;
        var del = _store.TryGet(functorId);
        if (del is null) return null;
        Func<Engine, int, bool> wrapper = (engine, cursor) => del(engine, cursor);
        _resumeCache[functorId] = wrapper;
        return wrapper;
    }

    public Func<Engine, bool>? OnDispatch(int targetAddress)
    {
        // Fast path: address has no associated predicate (it's a launcher
        // stub or an unindexed clause body). Nothing to promote.
        if (!_predicatesByAddress.TryGetValue(targetAddress, out var pred))
            return null;

        int functorId = pred.FunctorId;

        // Chunk 75 — JIT indexing profile. Every Call/Execute dispatch
        // bumps the predicate's runtime call count; once a dynamic
        // predicate crosses the threshold the next query setup
        // recompiles it with indexing enabled.
        _jitProfile.RecordCall(functorId);

        // Already promoted? Return the cached delegate immediately.
        var existing = _store.TryGet(functorId);
        if (existing is not null) return Wrap(existing);

        // Fast path for already-rejected predicates. Without this,
        // every call to a dynamic / oversized / layout-excluded
        // predicate (the majority of dispatches in a real program)
        // pays the full RecordInvocation entry sequence (3-5 dict
        // ops) on top of the bytecode dispatch — a 7× slowdown over
        // Tier-0 on Blint. The store's _unpromotable set is the
        // exact answer to "is this functor a wasted RecordInvocation
        // call?" so check it directly and bail.
        if (_store.IsUnpromotable(functorId)) return null;

        // Otherwise let the store decide whether the counter has crossed
        // the threshold and a compile should fire now. Hand the
        // callee map through so IL Call eligibility can be evaluated.
        var fresh = _store.RecordInvocation(functorId, pred, _calleeMap);
        return fresh is null ? null : Wrap(fresh);
    }

    private static Func<Engine, bool> Wrap(Shumway.Compiler.Il.PredicateDelegate del)
        // Fresh call from a Call/Execute dispatch — clauseCursor 0.
        // Re-entries from backtracking go through the engine's IL CP
        // machinery directly, not back through this adapter.
        => engine => del(engine, 0);
}
