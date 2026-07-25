using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-037 — a <c>( Cond *-> Then ; Else )</c> that is NOT inline-eligible (a cut
/// in a branch, nested control in a part, or a standalone <c>*-></c> with no else)
/// lowers to a synthesized soft-cut helper: clause 1 = <c>'$choice_level'(K), Cond,
/// '$soft_cut'(K), Then</c> and clause 2 = <c>Else</c>, so <c>Else</c> is pruned
/// once <c>Cond</c> succeeds while <c>Cond</c>'s choice points (and thus its
/// non-determinism) survive. A cut in a branch stays transparent to the host clause.
/// </summary>
public class Adr037NonEligibleTests
{
    private static PrologEngine Load(string program)
    {
        var e = new PrologEngine();
        e.ConsultString(program);
        return e;
    }

    [Fact]
    public void CutInThen_IsTransparentToHostClause()
    {
        // The ! in Then commits the HOST: it prunes member's remaining choice
        // points, so only the first solution survives.
        var e = Load("p(R) :- ( member(X, [1,2,3]) *-> R = X, ! ; R = none ).\n");
        Assert.True(e.Query("findall(R, p(R), L), L == [1].").Success);
    }

    [Fact]
    public void NestedControlInThen_Works()
    {
        var e = Load("q(R) :- ( true *-> ( R = a ; R = b ) ; R = none ).\n");
        Assert.True(e.Query("findall(R, q(R), L), L == [a,b].").Success);
    }

    [Fact]
    public void NonEligible_PreservesCondNondeterminism_ElsePruned()
    {
        // member (non-det) *-> a nested disjunction: every combination, no `none`.
        var e = Load("p(R) :- ( member(X, [1,2,3]) *-> ( R = X ; R = neg(X) ) ; R = none ).\n");
        Assert.True(e.Query(
            "findall(R, p(R), L), L == [1, neg(1), 2, neg(2), 3, neg(3)].").Success);
    }

    [Fact]
    public void NonEligible_CondFails_RunsElse()
    {
        var e = Load("s(R) :- ( member(_, []) *-> ( R = a ; R = b ) ; R = els ).\n");
        Assert.True(e.Query("findall(R, s(R), L), L == [els].").Success);
    }

    [Fact]
    public void Standalone_SoftCut_NoElse()
    {
        var e = Load(
            "r(R) :- ( 1 =< 1 *-> R = yes ).\n" +
            "f(R) :- ( fail *-> R = yes ).\n");
        Assert.True(e.Query("r(R), R == yes.").Success);
        Assert.False(e.Query("f(_).").Success);   // no else, cond fails → fail
        // Non-deterministic standalone condition still enumerates.
        var e2 = Load("g(X) :- ( member(X, [1,2,3]) *-> true ).\n");
        Assert.True(e2.Query("findall(X, g(X), L), L == [1,2,3].").Success);
    }

    // ---- runtime-built ( C *-> T ; E ) via call/1 ----

    [Fact]
    public void RuntimeBuilt_SoftCut_PreservesNondeterminism()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "findall(X-R, ( G = ( member(X,[1,2,3]) *-> R = t(X) ; R = none ), call(G) ), L), " +
            "L == [1-t(1), 2-t(2), 3-t(3)].").Success);
    }

    [Fact]
    public void RuntimeBuilt_SoftCut_CondFails_RunsElse()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "findall(R, ( G = ( fail *-> R = a ; R = els ), call(G) ), L), L == [els].").Success);
    }

    [Fact]
    public void RuntimeBuilt_BareSoftCut_IsConjunction()
    {
        var e = new PrologEngine();
        // ( C *-> T ) with no else is just ( C, T ).
        Assert.True(e.Query(
            "findall(X, ( G = ( member(X,[1,2,3]) *-> true ), call(G) ), L), L == [1,2,3].").Success);
        Assert.False(e.Query("G = ( fail *-> true ), call(G).").Success);
    }

    [Fact]
    public void RuntimeBuilt_Arrow_CommitsFirstSolution()
    {
        // Regression: a runtime-built ( C -> T ; E ) used to run BOTH branches when
        // C succeeded (the $mqual module distribution hid the -> from $call_disj's
        // if-then-else clause). WrapGoal distributes INTO the ->, so it commits.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "findall(R, ( G = ( true -> R = then ; R = else ), call(G) ), L), L == [then].").Success);
        Assert.True(e.Query(
            "findall(R, ( G = ( fail -> R = then ; R = else ), call(G) ), L), L == [else].").Success);
    }

    [Fact]
    public void NonEligible_DeterministicCond_LeavesNoChoicePoint()
    {
        // A cut in Then makes it non-eligible (helper path) AND deterministic: the
        // deterministic condition plus the commit leave no choice point.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "'$choice_level'(B0), ( X = 1 *-> Y = X, ! ; Y = 2 ), '$choice_level'(B1), " +
            "X == 1, Y == 1, B1 =:= B0.").Success);
    }
}
