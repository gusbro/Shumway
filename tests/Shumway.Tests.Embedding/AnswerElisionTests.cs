using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// How much of an answer a top level prints. `numlist(1, 10000000, X)` has an
/// answer nobody wants delivered in full, so a list is cut off at
/// `answer_max_depth` elements and ends the way every top level says there is
/// more: `|...`.
///
/// <para>This is a DISPLAY rule. write/1 still prints what it is given — a
/// program's output is not a summary of itself.</para>
/// </summary>
public sealed class AnswerElisionTests
{
    private static string Answer(PrologEngine engine, string query)
    {
        using var run = new TopLevelSession(engine).StartQuery(query);
        Assert.True(run.MoveNext());
        return run.Format(200);
    }

    private static PrologEngine Engine() => new() { Out = new StringWriter() };

    [Fact]
    public void ALongListIsCutOff()
    {
        var e = Engine();
        e.Flags.AnswerMaxDepth = 5;
        Assert.Equal("X = [1, 2, 3, 4, 5 | ...]", Answer(e, "numlist(1, 1000, X)."));
    }

    [Fact]
    public void AShortListIsLeftAlone()
    {
        var e = Engine();
        e.Flags.AnswerMaxDepth = 5;
        Assert.Equal("X = [1, 2, 3]", Answer(e, "X = [1,2,3]."));
    }

    [Fact]
    public void DeepNestingIsCutOff()
    {
        var e = Engine();
        e.Flags.AnswerMaxDepth = 3;
        Assert.Equal("X = f(g(h(...)))", Answer(e, "X = f(g(h(i(j))))."));
    }

    [Fact]
    public void ZeroMeansEverything()
    {
        var e = Engine();
        e.Flags.AnswerMaxDepth = 0;
        Assert.Equal("X = [1, 2, 3, 4, 5, 6]", Answer(e, "numlist(1, 6, X)."));
    }

    [Fact]
    public void TheFlagIsReadableAndWritableFromProlog()
    {
        var e = Engine();
        Assert.True(e.Query("current_prolog_flag(answer_max_depth, 100).").Success);
        Assert.True(e.Query("set_prolog_flag(answer_max_depth, 7).").Success);
        Assert.Equal(7, e.Flags.AnswerMaxDepth);
        Assert.True(e.Query("current_prolog_flag(answer_max_depth, 7).").Success);
    }

    [Fact]
    public void TheFlagRefusesWhatIsNotACount()
    {
        var e = Engine();
        Assert.Contains("type_error",
            Assert.Throws<ShumwayPrologException>(
                () => e.Query("set_prolog_flag(answer_max_depth, foo).")).Message);
        Assert.Contains("domain_error",
            Assert.Throws<ShumwayPrologException>(
                () => e.Query("set_prolog_flag(answer_max_depth, -1).")).Message);
    }

    [Fact]
    public void WriteIsNotElided()
    {
        // The program's own output is untouched: only the ANSWER is a summary.
        var sink = new StringWriter();
        var e = new PrologEngine { Out = sink };
        e.Flags.AnswerMaxDepth = 3;
        Assert.True(e.Query("numlist(1, 10, L), write(L).").Success);
        Assert.Equal("[1,2,3,4,5,6,7,8,9,10]", sink.ToString());
    }

    [Fact]
    public void NestingDoesNotMultiplyTheAnswer()
    {
        // Per-list elision alone is not a bound: a list of lists shows the
        // limit SQUARED. This answer respected every per-list and per-depth
        // rule and still came to 55,000 characters.
        var e = new PrologEngine();
        var sol = e.Query("findall(L, (between(1, 1000, X), length(L, X)), Ls).");
        Assert.True(sol.Success);
        string shown = SolutionFormatter.Format(
            e, sol, new[] { "Ls" }, 1200);
        Assert.True(shown.Length < 2000, $"answer is {shown.Length} characters");
        Assert.Contains("| ...", shown);
    }

    [Fact]
    public void TheBudgetIsSpentOnTheFrontOfTheAnswer()
    {
        // What survives has to be the BEGINNING: an elision that dropped the
        // first elements would be worse than no elision at all.
        var e = new PrologEngine();
        var sol = e.Query("findall(L, (between(1, 1000, X), length(L, X)), Ls).");
        string shown = SolutionFormatter.Format(e, sol, new[] { "Ls" }, 1200);
        // The first sublist has one element, the second two: both intact.
        Assert.Matches(@"Ls = \[\[_[A-Za-z0-9]+\], \[_[A-Za-z0-9]+, _[A-Za-z0-9]+\],", shown);
    }
}

/// <summary>
/// <c>statistics/0</c> — the report a person reads after a run. Its counters
/// are the RUNNING activation's, because that is where a heap and a trail
/// exist: they belong to the query in progress.
/// </summary>
public sealed class Statistics0Tests
{
    private static string Report(string goal)
    {
        var sink = new StringWriter();
        var e = new PrologEngine { Out = sink };
        Assert.True(e.Query(goal).Success);
        return sink.ToString();
    }

    [Fact]
    public void ReportsTimeAndMemory()
    {
        string text = Report("statistics.");
        Assert.Contains("Runtime:", text);
        Assert.Contains("Walltime:", text);
        Assert.Contains("Heap:", text);
        Assert.Contains("Trail:", text);
        Assert.Contains("Stack:", text);
    }

    [Fact]
    public void TheHeapFigureFollowsWhatTheQueryBuilt()
    {
        // A query that built a 200,000-element list has used more heap than one
        // that built nothing — the number has to be the live activation's, not a
        // constant.
        string idle = Report("statistics.");
        string busy = Report("numlist(1, 200000, _), statistics.");
        Assert.Contains("Heap:      0 cells", idle);
        Assert.DoesNotContain("Heap:      0 cells", busy);
    }
}
