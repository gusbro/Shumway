using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A hook that expands its own output never finishes, and the
/// expansion is applied to the output and its subgoals by design, so no
/// system can promise termination here. What a system can promise is that
/// asking for it does not kill the process: this one used to walk the
/// expansion on the machine stack and die of a stack overflow, which no
/// program can catch and which takes the host down with it.
///
/// <para>The walk is iterative now, and a budget bounds how many times a term
/// or a goal may be replaced along one path. Running out is a resource error,
/// catchable like any other, rather than a half-expanded clause nobody wrote
/// or a load that never returns.</para></summary>
public sealed class ExpansionRunawayTests
{
    private static ShumwayPrologException Raises(string program)
        => Assert.Throws<ShumwayPrologException>(() => new PrologEngine().ConsultString(program));

    private static void IsExpansionRunaway(ShumwayPrologException ex)
        => Assert.Contains("resource_error(expansion_depth)", ex.Message.Replace(" ", ""));

    [Fact]
    public void AGoalThatExpandsIntoItselfIsReported()
    {
        // The reported shape: the expansion of p(_) contains p(_), so every
        // pass has one more of them to expand.
        IsExpansionRunaway(Raises(
            "b((_, p(_))).\n"
            + "goal_expansion(p(B), (B)) :- b(B).\n"
            + "p :- p(_).\n"));
    }

    [Fact]
    public void TheEngineIsStillThereAfterwards()
    {
        // The point of the exercise. A stack overflow ends the process; this
        // is a ball a program catches, and the engine keeps working.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(consult_text('b((_, p(_))). "
            + "goal_expansion(p(B), (B)) :- b(B). p :- p(_).'), "
            + "error(resource_error(expansion_depth), _), true).").Success);
        Assert.True(e.Query("X is 1 + 1, X == 2.").Success);
    }

    [Theory]
    // A cycle of more than one step is the same runaway: a rewrites to b and
    // b back to a, so it never settles. It used to stop quietly after a fixed
    // few passes and keep whichever of the two came up last.
    [InlineData("goal_expansion(aa, bb).\ngoal_expansion(bb, aa).\np :- aa.\n")]
    [InlineData("term_expansion(one, two).\nterm_expansion(two, one).\none.\n")]
    public void ACycleOfSeveralStepsIsReportedToo(string program)
        => IsExpansionRunaway(Raises(program));

    [Fact]
    public void ABudgetIsPerPathAndNotPerClause()
    {
        // Five hundred goals, each expanded once, is not a runaway: the
        // budget bounds a goal that keeps expanding into itself, not a clause
        // that simply has a lot of goals in it. A budget spent across the
        // whole clause would refuse this program.
        string goals = string.Join(", ", Enumerable.Range(0, 500).Select(i => $"g({i})"));
        var e = new PrologEngine();
        e.ConsultString(
            "goal_expansion(g(N), h(N)).\n"
            + "g(_) :- throw(not_expanded).\n"
            + "h(_).\n"
            + $"p :- {goals}.\n");
        Assert.True(e.Query("p.").Success);
    }

    [Fact]
    public void AChainLongerThanTheOldCapNowConverges()
    {
        // The bound used to be a handful of passes, silently applied: a chain
        // of twenty rewrites stopped in the middle and compiled whatever it
        // had reached. It runs to the end now.
        var e = new PrologEngine();
        e.ConsultString(
            "goal_expansion(s(N), s(M)) :- integer(N), N > 0, M is N - 1.\n"
            + "s(N) :- N == 0.\n"
            + "p :- s(20).\n");
        Assert.True(e.Query("p.").Success);
    }
}
