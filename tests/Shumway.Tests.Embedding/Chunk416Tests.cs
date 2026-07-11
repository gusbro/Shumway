using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 416 (Phase 29, ADR-021 candidate) — the per-engine meta-call route
/// cache (<c>Activation.MetaRouteCache</c>, see <c>MetaRoute.cs</c>). A runtime
/// meta-call's dispatch decision is cached per (goal atom id, total arity)
/// and replayed on repeat goals. These tests pin the discriminating cases:
/// every scenario repeats the SAME goal functor within one query, so the
/// second and later dispatches take the cached route — a wrong or stale
/// route diverges visibly.
/// </summary>
public class Chunk416Tests
{
    private static PrologEngine Make(string source)
    {
        var engine = new PrologEngine();
        engine.ConsultString(source);
        return engine;
    }

    [Fact]
    public void RepeatedUserGoal_TakesCachedJumpRoute()
    {
        // Same functor meta-called in a loop: dispatch 1 fills the cache,
        // dispatches 2..5 replay the Jump route. All must reach p/1.
        var e = Make(
            "p(X) :- X > 0.\n" +
            "loop(0).\n" +
            "loop(N) :- mk(N, G), call(G), M is N - 1, loop(M).\n" +
            "mk(N, p(N)).\n");
        Assert.True(e.Query("loop(5).").Success);
    }

    [Fact]
    public void RepeatedConjunction_BarrierRouteCutsCorrectly()
    {
        // (member(X,..), !) as a runtime goal TWICE: both runs must route via
        // $call_conj with the barrier in X2 — a `!` inside the runtime goal
        // commits to the call, not further (chunk 88). If the cached
        // BarrierHelperJump route failed to re-store the barrier, the second
        // run's cut would prune the caller's gen/1 choice points.
        var e = Make(
            "gen(1). gen(2).\n" +
            "once_first(L, X) :- mk(L, X, G), call(G).\n" +
            "mk(L, X, (member(X, L), !)).\n" +
            "go(S-X) :- gen(S), once_first([a, b], X).\n");
        var all = e.QueryAll("go(R).").ToList();
        // Both gen solutions survive; each picks only member's first.
        Assert.Equal(2, all.Count);
        Assert.Equal("-(1, a)", all[0]["R"]!.ToString());
        Assert.Equal("-(2, a)", all[1]["R"]!.ToString());
    }

    [Fact]
    public void RepeatedCutGoal_CachedCutRouteHonoursBarrier()
    {
        // call(!) twice in one clause body: ISO says a `!` as a call/1 goal
        // is local to the call — backtracking into gen/1 must survive both.
        var e = Make(
            "gen(1). gen(2).\n" +
            "go(X) :- gen(X), mkcut(C), call(C), call(C).\n" +
            "mkcut(!).\n");
        var all = e.QueryAll("go(X).").ToList();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void RepeatedClosure_CallNExtraArgsViaScratch()
    {
        // call/3 closure invoked repeatedly: the extra args ride the
        // per-engine scratch buffer. Values must not bleed across calls.
        var e = Make(
            "add(A, B, C) :- C is A + B.\n" +
            "go(R1, R2) :- mk(G), call(G, 1, R1), call(G, 10, R2).\n" +
            "mk(add(5)).\n");
        var s = e.Query("go(R1, R2).");
        Assert.True(s.Success);
        Assert.Equal("6", s["R1"]!.ToString());
        Assert.Equal("15", s["R2"]!.ToString());
    }

    [Fact]
    public void RepeatedBuiltinGoal_CachedBuiltinRoute()
    {
        // A builtin as the runtime goal, repeated: second hit takes the
        // cached Builtin route (impl invocation, error stamping intact).
        var e = Make(
            "go :- mk(2, G1), call(G1), mk(3, G2), call(G2).\n" +
            "mk(N, integer(N)).\n");
        Assert.True(e.Query("go.").Success);
    }

    [Fact]
    public void MidQueryAutoPromotion_ResolvesThroughCache()
    {
        // chunk-207 scenario THROUGH the cache: assertz auto-promotes zzz/1
        // mid-query (materialising a trampoline the query's link never saw);
        // the first call(zzz(1)) resolves it via the slow path and caches the
        // route, the second replays it. A stale or wrongly keyed entry would
        // miss the trampoline.
        // (NOTE: `catch(call(zzz(1)),_,true), assertz(zzz(1))` — a caught
        // existence_error BEFORE the assertz — fails, but identically on the
        // pre-cache build: a pre-existing engine issue, not a cache one.)
        var e = Make("ok.\n");
        Assert.True(e.Query(
            "assertz(zzz(1)), call(zzz(1)), call(zzz(1)).").Success);
    }

    [Fact]
    public void RepeatedDisjunction_EnumeratesBothBranchesEachTime()
    {
        // (X = a ; X = b) as a runtime goal inside findall, twice: the
        // cached $call_disj route must keep full backtracking.
        var e = Make(
            "go(L1, L2) :- mk(X1, G1), findall(X1, call(G1), L1),\n" +
            "              mk(X2, G2), findall(X2, call(G2), L2).\n" +
            "mk(X, (X = a ; X = b)).\n");
        var s = e.Query("go(L1, L2).");
        Assert.True(s.Success);
        Assert.Equal(".(a, .(b, []))", s["L1"]!.ToString());
        Assert.Equal(".(a, .(b, []))", s["L2"]!.ToString());
    }

    [Fact]
    public void CacheIsPerQuery_SecondQueryRelinksAddresses()
    {
        // The cache is stamped with the query's address map: a second query
        // must not replay the first query's addresses (the dynamic region
        // re-links between queries, so a stale Jump address would land in
        // arbitrary code).
        var e = Make(
            ":- dynamic d/1.\n" +
            "run(X) :- mk(X, G), call(G).\n" +
            "mk(X, d(X)).\n");
        Assert.True(e.Query("assertz(d(1)), run(1).").Success);
        Assert.True(e.Query("assertz(d(2)), run(2).").Success);
        var all = e.QueryAll("run(X).").ToList();
        Assert.Equal(2, all.Count);
    }
}
