using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Solution.IsLast lets an interactive top-level detect when no choice
/// point remains so it can finish without the `;` prompt and without a
/// trailing `false`. This mirrors GNU Prolog's documented mechanism:
/// "in some cases the top-level can detect that the current solution is
/// the last one (no more alternatives remaining)" — it's a physical
/// choice-point check, true exactly when the engine has no query-local
/// CP left when the solution is produced.
///
/// <para>A disjunction's last branch, a cut, and a single-clause
/// predicate are detected. <c>member/2</c> is too: the prelude defines
/// it in the first-argument-indexed look-ahead form (the GNU Prolog
/// library shape), so the final element of a fixed list leaves no
/// choice point and is flagged last — <c>member(A,[x,y])</c> finishes
/// at <c>A = y</c> with no prompt, exactly like gprolog.</para>
/// </summary>
public class SolutionDeterminismTests
{
    [Fact]
    public void DeterministicGoal_FirstSolutionIsLast()
    {
        var engine = new PrologEngine();
        var sols = engine.QueryAll("X = hello.").ToList();
        Assert.Single(sols);
        Assert.True(sols[0].IsLast);
    }

    [Fact]
    public void CutGoal_IsLast()
    {
        var engine = new PrologEngine();
        var sols = engine.QueryAll("member(X, [a,b,c]), !.").ToList();
        Assert.Single(sols);
        Assert.Equal("a", sols[0].Bindings["X"].ToString());
        Assert.True(sols[0].IsLast);
    }

    [Fact]
    public void Disjunction_LastBranchIsLast()
    {
        // GNU Prolog's canonical example: (X=1 ; X=2) stops at X=2 with
        // no prompt — taking the last branch of ;/2 leaves no CP.
        var engine = new PrologEngine();
        var sols = engine.QueryAll("(X = 1 ; X = 2).").ToList();
        Assert.Equal(2, sols.Count);
        Assert.False(sols[0].IsLast);   // first branch leaves the ; CP
        Assert.True(sols[1].IsLast);    // last branch — CP exhausted
    }

    [Fact]
    public void SingleClausePredicate_IsLast()
    {
        var engine = new PrologEngine();
        engine.ConsultString("only_fact(yes).\n");
        var sols = engine.QueryAll("only_fact(X).").ToList();
        Assert.Single(sols);
        Assert.True(sols[0].IsLast);
    }

    [Fact]
    public void Member_FinalElementIsLast()
    {
        // The first-arg-indexed prelude member leaves no CP on the final
        // element, so the last solution is flagged last — gprolog
        // behaviour. member(X,[x,y]) finishes at X=y with no prompt.
        var engine = new PrologEngine();
        var sols = engine.QueryAll("member(X, [x, y]).").ToList();
        Assert.Equal(2, sols.Count);
        Assert.False(sols[0].IsLast);   // x leaves the CP for the tail
        Assert.True(sols[1].IsLast);    // y is the last — no residual CP
    }

    [Fact]
    public void Member_SingletonIsLast()
    {
        var engine = new PrologEngine();
        var sols = engine.QueryAll("member(X, [only]).").ToList();
        Assert.Single(sols);
        Assert.True(sols[0].IsLast);
    }
}
