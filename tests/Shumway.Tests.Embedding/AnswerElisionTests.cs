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
}
