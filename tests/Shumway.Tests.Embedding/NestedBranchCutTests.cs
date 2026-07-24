using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A cut-transparent <c>!</c> nested inside a <c>-&gt;</c> inside a <c>;</c> must
/// commit the HOST clause (ISO 7.8.8). The barrier that carries the host's cut
/// level is threaded through every synthesized disjunction helper; a regression
/// (<c>ReplaceTransparentCuts</c> not descending into a nested <c>;</c>/<c>-&gt;</c>)
/// left the barrier variable out of the OUTER helper's head, so the INNER helper
/// read a garbage barrier — a crash (<c>IndexOutOfRangeException</c> in
/// <c>Activation.Cut</c>) surfaced by running such a predicate under
/// <c>findall/3</c>.
/// </summary>
public class NestedBranchCutTests
{
    [Fact]
    public void NestedCutInThenInDisjunction_UnderFindall_DoesNotCrash()
    {
        var e = new PrologEngine();
        e.ConsultString(
            "sq :- ( true, ( 1 =< 1 -> ! ; true ) ; write(b2) ).\n");
        // Before the fix this threw IndexOutOfRangeException inside Cut.
        Assert.True(e.Query("findall(t, sq, L), L == [t].").Success);
    }

    [Fact]
    public void NestedCutInThenInDisjunction_DirectCall_Succeeds()
    {
        var e = new PrologEngine();
        e.ConsultString(
            "sq :- ( true, ( 1 =< 1 -> ! ; true ) ; fail ).\n");
        Assert.True(e.Query("sq.").Success);
        // In a failure-driven loop it must terminate (no runaway).
        Assert.True(e.Query("( sq, fail ; true ).").Success);
    }

    [Fact]
    public void NestedBranchCut_IsTransparentToHostClause()
    {
        // The ! in the then commits h's clause: g/1 is nondeterministic, so a !
        // transparent to h prunes h's remaining solutions — only the first
        // survives. (A ! that only cut the helper would leave [1,2,3].)
        var e = new PrologEngine();
        e.ConsultString(
            "g(1).\ng(2).\ng(3).\n" +
            "h(X) :- g(X), ( X >= 1 -> ! ; true ).\n");
        Assert.True(e.Query("findall(X, h(X), L), L == [1].").Success);
    }

    [Fact]
    public void DoublyNestedBranchCut_UnderFindall_DoesNotCrash()
    {
        // Cut nested two disjunctions deep, to exercise the barrier threading at
        // more than one helper level.
        var e = new PrologEngine();
        e.ConsultString(
            "p :- ( a ; ( b ; ( 1 =< 1 -> ! ; true ) ) ).\n" +
            "a :- fail.\n" +
            "b :- fail.\n");
        Assert.True(e.Query("findall(t, p, L), L == [t].").Success);
    }
}
