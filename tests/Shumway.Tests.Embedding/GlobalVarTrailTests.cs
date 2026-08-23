using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Backtrackable global variables: <c>b_setval/2</c> now trails the previous
/// value (Activation.TrailExternal → TrailType.MutableSet), so unwinding past
/// the write restores it — the Scryer <c>bb_b_put/2</c> contract clpz's
/// propagation state depends on. Plus the Scryer primitive shims
/// (<c>'$store_global_var'</c>, <c>'$store_backtrackable_global_var'</c>,
/// <c>'$fetch_global_var'</c>) behind iso_ext's bb_put / bb_b_put / bb_get.
/// </summary>
public class GlobalVarTrailTests
{
    private static bool Holds(string goal) => new PrologEngine().Query(goal).Success;

    [Fact]
    public void BSetval_RestoresPreviousValueOnBacktrack()
        => Assert.True(Holds(
            "b_setval(k, 1), ( b_setval(k, 2), fail ; b_getval(k, V) ), V == 1."));

    [Fact]
    public void BSetval_CreatedKeyIsRemovedOnBacktrack()
        => Assert.True(Holds(
            "( b_setval(fresh_k, 9), fail ; true ), \\+ '$fetch_global_var'(fresh_k, _)."));

    [Fact]
    public void NbSetval_SurvivesBacktrack()
        => Assert.True(Holds(
            "nb_setval(k2, 1), ( nb_setval(k2, 2), fail ; nb_getval(k2, V) ), V == 2."));

    [Fact]
    public void BSetval_SurvivesCutThenRestoresAboveIt()
        // A commit (once/1's internal cut runs CompactTrails) between the write
        // and the backtrack must not discard the restore entry
        // (TrailType.MutableSet always survives compaction).
        => Assert.True(Holds(
            "b_setval(kc, 1), "
            + "( once(( b_setval(kc, 2), member(_, [a, b]) )), fail "
            + "; b_getval(kc, V) ), V == 1."));

    [Fact]
    public void ScryerPrimitives_StoreFetch()
        => Assert.True(Holds(
            "'$store_global_var'(sk, hello), '$fetch_global_var'(sk, V), V == hello."));

    [Fact]
    public void FetchGlobalVar_FailsForUnsetKey()
        => Assert.True(Holds("\\+ '$fetch_global_var'(never_set_key_xyz, _)."));

    [Fact]
    public void Blackboard_PreservesAttributedVariables()
    {
        // The SICStus/Trealla contract: a value carrying attvars is stored
        // residualized (copy_term/3) and every bb_get re-runs the projection
        // goals on a fresh copy — constraints survive, reads are independent,
        // and the original variable is untouched.
        var e = new PrologEngine();
        e.Query("use_module(library(coroutining)).");
        // frozen goal survives and fires when the RETRIEVED COPY is bound
        // (observed by side effect: residualization copies the goal's outer
        // variables too, so a binding would land on the copy)
        Assert.True(e.Query(
            "freeze(V, nb_setval(bbfired, yes)), bb_put(bbk1, V), "
            + "bb_get(bbk1, W), W = 1, nb_getval(bbfired, yes), var(V).").Success);
        // sharing inside a compound is preserved across the round-trip
        Assert.True(e.Query(
            "freeze(B, true), bb_put(bbk2, f(B, g(B))), "
            + "bb_get(bbk2, f(P, g(Q))), P == Q.").Success);
        // dif survives; each bb_get is an independent copy
        Assert.True(e.Query(
            "dif(C, b), bb_put(bbk3, C), "
            + "bb_get(bbk3, Z1), \\+ Z1 = b, "
            + "bb_get(bbk3, Z2), Z2 = c.").Success);
        // plain values keep the raw path
        Assert.True(e.Query(
            "bb_put(bbk4, hello(world)), bb_get(bbk4, hello(world)).").Success);
    }

    [Fact]
    public void BbGet_FailsCleanlyForUnsetKey()
        // bb_get's body is an inline catch in the BAKED prelude: its recovery
        // is the bare '$catchrec_N' the catch frame stores, compiled as
        // '$prelude$$catchrec_N'. The bare alias must come from the '$$' seam --
        // splitting the mangled name at the FIRST '$' sees no module and drops
        // the alias, so the recovery dispatch had no address and this query
        // crashed the engine instead of failing (their clpz's bb_get-based
        // global state was the finder).
        => Assert.True(Holds("\\+ bb_get(never_set_key_bbq, _)."));

    [Fact]
    public void ScryerBacktrackablePrimitive_Restores()
        => Assert.True(Holds(
            "'$store_global_var'(bk, a), "
            + "( '$store_backtrackable_global_var'(bk, b), fail "
            + "; '$fetch_global_var'(bk, V) ), V == a."));

    [Fact]
    public void IsoExt_BbPut_BbGet_BbBPut_ViaRealLibraryShape()
    {
        // The Scryer doc example: bb_put survives backtracking, bb_b_put
        // reverts. Uses the primitives directly (the iso_ext.pl wrappers are
        // one atom-check away from these).
        Assert.True(Holds(
            "'$store_global_var'(city, valladolid), "
            + "( '$store_global_var'(city, salamanca), fail "
            + "; '$fetch_global_var'(city, X) ), X == salamanca."));
        Assert.True(Holds(
            "'$store_global_var'(city2, valladolid), "
            + "( '$store_backtrackable_global_var'(city2, salamanca), fail "
            + "; '$fetch_global_var'(city2, X) ), X == valladolid."));
    }
}
