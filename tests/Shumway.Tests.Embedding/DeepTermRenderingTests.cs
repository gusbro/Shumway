using System.IO;
using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>How deeply a term nests is the program's choice, and both
/// renderers spend a C# frame per level. A .NET stack overflow cannot be
/// caught: it takes the process down with no goal to unwind and nothing to
/// report, so `write/1` on a term nested past the stack killed the host
/// outright. RecursionGuard turns that into an ordinary catchable ball, which
/// is the treatment the reader and the clause pipeline already had.
///
/// <para>Reporting the refusal must not be able to fail either. The error
/// printer renders the culprit, and the stack is not necessarily unwound when
/// it runs, so the refusal can repeat there on a term of any size. It falls
/// back to a spelling that walks nothing.</para></summary>
public sealed class DeepTermRenderingTests
{
    // Deep enough to exhaust any stack the suite might run on, including the
    // 64 MB one the CLIs get.
    private const int Deep = 200_000;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            nest(0, x) :- !.
            nest(N, f(T)) :- M is N - 1, nest(M, T).
            """);
        return e;
    }

    [Theory]
    [InlineData("write(X)")]
    [InlineData("writeq(X)")]
    [InlineData("print(X)")]
    [InlineData("write_canonical(X)")]
    public void WritingATermTooDeepToRender_IsACatchableBall(string goal)
    {
        var e = Engine();
        var sol = e.Query(
            $"nest({Deep}, X), catch({goal}, error(E, _), true), "
            + "E = resource_error(term_nesting).");
        Assert.True(sol.Success, $"{goal} did not refuse with a catchable ball");
    }

    /// <summary>ANTI-VACUITY: the guard fires on depth, not on everything. A
    /// term a stack comfortably holds still renders, and renders exactly.
    /// </summary>
    [Fact]
    public void ATermTheStackHolds_StillRendersExactly()
    {
        var e = Engine();
        var text = new StringWriter();
        e.Out = text;
        Assert.True(e.Query("nest(100, X), write(X).").Success);
        string shown = text.ToString();
        Assert.Equal(100, System.Text.RegularExpressions.Regex.Matches(shown, @"f\(").Count);
        Assert.EndsWith(new string(')', 100), shown);
    }

    /// <summary>The answer display goes through the OTHER renderer, which is
    /// iterative, so with elision off it RENDERS the whole thing rather than
    /// refusing. Safe is not the goal; capable is.</summary>
    [Fact]
    public void TheAnswerDisplayRendersAnyDepth()
    {
        var e = Engine();
        e.Flags.AnswerMaxDepth = 0;                 // the user asked for it all
        using var run = new TopLevelSession(e).StartQuery($"nest({Deep}, X).");
        Assert.True(run.MoveNext());
        string shown = run.Format(200);
        Assert.StartsWith("X = f(f(", shown);
        Assert.True(shown.Length > Deep, $"only {shown.Length} chars came out");
    }

    /// <summary>And the report of that refusal names it, rather than coming
    /// out as a bare `...`: describing the ball must not need the walk that
    /// just failed.</summary>
    [Fact]
    public void TheReportOfTheRefusalNamesIt()
    {
        var e = Engine();
        var described = ErrorRendering.Describe(
            e, new Shumway.Core.PrologRuntimeException("resource_error", "term_nesting"));
        Assert.Contains("resource_error(term_nesting)",
                        string.Concat(described));
    }
}
