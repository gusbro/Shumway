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
