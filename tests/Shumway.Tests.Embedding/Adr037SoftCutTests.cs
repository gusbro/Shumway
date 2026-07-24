using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-037 — soft cut. <c>( Cond *-&gt; Then ; Else )</c> lowers inline to
/// <c>try_me_else; get_level_b; Cond; soft_cut; Then ; ELSE: trust_me; Else</c>.
/// Unlike <c>-&gt;</c>, it does NOT commit to Cond's first solution: Then runs for
/// every solution of Cond, Else runs only when Cond has none. The distinctive
/// property is that Cond's non-determinism survives the commit.
/// </summary>
public class Adr037SoftCutTests
{
    private static PrologEngine Load(string program)
    {
        var e = new PrologEngine();
        e.ConsultString(program);
        return e;
    }

    [Fact]
    public void SoftCut_TakesThen_WhenCondSucceeds()
    {
        var e = Load("pick(X, R) :- ( member(X, [1,2,3]) *-> R = got(X) ; R = none ).\n");
        Assert.True(e.Query("pick(X, R), X == 1, R == got(1).").Success);
    }

    [Fact]
    public void SoftCut_RunsElse_WhenCondFails()
    {
        var e = Load("pick(X, R) :- ( member(X, []) *-> R = got(X) ; R = none ).\n");
        Assert.True(e.Query("pick(_, R), R == none.").Success);
    }

    [Fact]
    public void SoftCut_ElseNotReachedOnBacktrack_WhenCondSucceeded()
    {
        // Once Cond succeeds, Else is pruned even under full backtracking.
        var e = Load("pick(X, R) :- ( member(X, [1,2,3]) *-> R = X ; R = none ).\n");
        Assert.True(e.Query("findall(R, pick(_, R), L), L == [1,2,3].").Success);
    }

    [Fact]
    public void SoftCut_PreservesCondNondeterminism_ThenPerSolution()
    {
        // THE distinguishing case vs ->: Then runs once per Cond solution, so all
        // three are produced (-> would commit to the first and give [t(1)]).
        var e = Load("go(X, R) :- ( member(X, [1,2,3]) *-> R = t(X) ; R = none ).\n");
        Assert.True(e.Query("findall(R, go(_, R), L), L == [t(1),t(2),t(3)].").Success);
    }

    [Fact]
    public void SoftCut_DeterministicCond_ElsePruned()
    {
        // A deterministic Cond leaves the *-> deterministic (no lingering CP that
        // would re-enter Else on backtracking): exactly one answer, from Then.
        var e = Load("d(R) :- ( X = 1 *-> R = X ; R = none ).\n");
        Assert.True(e.Query("findall(R, d(R), L), L == [1].").Success);
    }

    [Fact]
    public void SoftCut_DeterministicCond_LeavesNoChoicePoint()
    {
        // The property the top-level determinism check reads: with a
        // deterministic condition the *-> leaves the choice-point level
        // unchanged (the ELSE CP is discarded, not just neutralised).
        var e = new PrologEngine();
        Assert.True(e.Query(
            "'$choice_level'(B0), ( true *-> true ; fail ), '$choice_level'(B1), B1 =:= B0.")
            .Success);
        // A non-deterministic condition keeps its choice point at the first answer.
        Assert.True(e.Query(
            "'$choice_level'(B0), ( member(_,[1,2,3]) *-> true ; fail ), '$choice_level'(B1), B1 > B0.")
            .Success);
    }

    [Fact]
    public void SoftCut_CondBindingsFlowIntoThen()
    {
        var e = Load(
            "p(1).\np(2).\n" +
            "f(R) :- ( p(X) *-> R = seen(X) ; R = none ).\n");
        // First Cond solution binds X=1; Then sees it.
        Assert.True(e.Query("f(R), R == seen(1).").Success);
        // And all solutions, since *-> keeps p/1's choice point.
        Assert.True(e.Query("findall(R, f(R), L), L == [seen(1), seen(2)].").Success);
    }

    [Fact]
    public void SoftCut_CondBindingsUndone_WhenElseRuns()
    {
        var e = Load(
            "f(In, R) :- ( In = bound(X) *-> R = then(X) ; R = els ).\n");
        // Cond fails to unify (In is a different shape); Else runs, X unbound.
        Assert.True(e.Query("f(other, R), R == els.").Success);
    }

    [Fact]
    public void SoftCut_CallCondition_Eligible()
    {
        // The time/1 shape: the condition is a call/1 of a variable goal.
        var e = Load("run(G, R) :- ( call(G) *-> R = ok ; R = no ).\n");
        Assert.True(e.Query("run(true, R), R == ok.").Success);
        Assert.True(e.Query("run(fail, R), R == no.").Success);
        // Non-determinism through call/1 survives too.
        Assert.True(e.Query("findall(X, run(member(X,[a,b]), _), L), L == [a,b].").Success);
    }

    // NOTE (follow-up): a *-> whose branches contain a cut, whose parts are
    // nested control constructs, or that is built at runtime is not yet lowered
    // (the inline path requires plain Then/Else and a plain-or-call condition);
    // such a *-> currently keeps the pre-ADR-037 behaviour. Covered by the
    // ADR-037 rollout's remaining stages, not this first cut.

    [Fact]
    public void Time1_IsDeterministic_ForDeterministicGoal()
    {
        // Regression: time(true) used to leave a spurious choice point (the ;
        // else branch), so the top-level offered ';'. With *-> the ELSE CP is
        // discarded once the deterministic goal succeeds. Measure the choice
        // level directly — the property the top-level determinism check reads.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "'$choice_level'(B0), time(true), '$choice_level'(B1), B1 =:= B0.").Success);
        // A non-deterministic goal still keeps its choice point at the first answer.
        Assert.True(e.Query(
            "'$choice_level'(B0), time(member(_,[1,2,3])), '$choice_level'(B1), B1 > B0.")
            .Success);
    }
}
