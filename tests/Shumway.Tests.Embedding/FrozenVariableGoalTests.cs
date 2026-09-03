using Shumway.Core;
using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Issue #76: a frozen goal that is still a VARIABLE. freeze/2
/// accepts one (it raises only when the goal runs), but every walker over
/// the suspension matched the conjunction pattern <c>(A, B)</c> first — and
/// unifying an unbound goal there BOUND it to a fresh conjunction, which
/// corrupted the suspension and recursed on the fresh halves. The reported
/// symptoms were an instantiation_error and a resource_error(memory) from
/// the residual projection; the merge and alias-check walkers hung outright.
/// Every walker now guards on var/1 first.</summary>
public class FrozenVariableGoalTests
{
    private static PrologEngine Coroutining()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.True(e.Query("use_module(library(coroutining)).").Success);
        return e;
    }

    private static TopLevelSession Session() => new(Coroutining());

    /// <summary>Runs <paramref name="body"/> on its own background thread and
    /// fails if it has not finished in 30 seconds. What these pin was a
    /// non-terminating walk: unbounded, a regression would hang the whole
    /// suite instead of reporting one red test. (xUnit's Timeout covers
    /// async tests only, and an engine query is synchronous.)</summary>
    private static void Bounded(Action body)
    {
        Exception? failure = null;
        var t = new System.Threading.Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)),
            "the goal did not terminate — the variable-goal walk is looping again");
        if (failure is not null)
            throw new Xunit.Sdk.XunitException($"{failure.GetType().Name}: {failure.Message}");
    }

    /// <summary>The one solution's answer text, bounded.</summary>
    private static string AnswerOf(string goal)
    {
        string answer = "";
        Bounded(() =>
        {
            using var run = Session().StartQuery(goal);
            Assert.True(run.MoveNext());
            answer = run.Format(200);
        });
        return answer;
    }

    [Fact]
    public void AVariableGoalProjectsAsFreeze()
    {
        // The issue's `freeze(X,Y).` — a resource_error(memory) before.
        Assert.Equal("freeze(X, Y)", AnswerOf("freeze(X, Y)."));
    }

    [Fact]
    public void AGoalThatIsTheFrozenVariableItselfProjects()
    {
        // The issue's `freeze(X,X).` — the goal IS the attributed variable,
        // so binding it inside the projection also woke it: an
        // instantiation_error out of a goal that had not run.
        Assert.Equal("freeze(X, X)", AnswerOf("freeze(X, X)."));
    }

    [Fact]
    public void AVariableInsideAConjunctionOfGoals()
    {
        string s = AnswerOf("freeze(X, (Y, true)).");
        Assert.Contains("freeze(X, Y)", s);
        Assert.Contains("freeze(X, true)", s);
    }

    [Fact]
    public void AliasingTwoVariableGoalsMerges()
    {
        // The merge walker ('$co_merge' / '$co_has') hung on this one.
        string s = AnswerOf("freeze(X, Y), freeze(Z, W), X = Z.");
        Assert.Contains("freeze(X, Y)", s);
        Assert.Contains("freeze(X, W)", s);
    }

    [Fact]
    public void TwoVariableGoalsOnTheSameVariable()
    {
        string s = AnswerOf("freeze(X, Y), freeze(X, Z).");
        Assert.Contains("freeze(X, Y)", s);
        Assert.Contains("freeze(X, Z)", s);
    }

    [Fact]
    public void RunningAVariableGoalIsAnInstantiationError()
    {
        // Delaying an unbound goal is fine; CALLING one is not.
        Bounded(() =>
        {
            var e = Coroutining();
            var re = Assert.Throws<PrologRuntimeException>(
                () => e.Query("freeze(X, Y), X = 1."));
            Assert.Equal("instantiation_error", re.Kind);
        });
    }

    [Fact]
    public void AGoalBoundBeforeTheWakeUpRunsNormally()
    {
        Bounded(() =>
        {
            var e = Coroutining();
            Assert.True(e.Query("freeze(X, Y), Y = true, X = 1.").Success);
        });
    }

    [Fact]
    public void AVariableSubConditionOfWhenIsAnInstantiationError()
    {
        // The same trap one level down: the sub-condition would have
        // unified with nonvar(_) and become a condition nobody wrote.
        Bounded(() =>
        {
            var e = Coroutining();
            var sol = e.Query(
                "catch(when((nonvar(_X), _C), true), error(E, _), true).");
            Assert.True(sol.Success);
            Assert.Equal("instantiation_error", sol["E"]!.ToString());
        });
    }
}
