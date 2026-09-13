using System.IO;
using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>How deeply a term nests is the program's choice, and both
/// renderers spend a C# frame per level. A .NET stack overflow cannot be
/// caught: it takes the process down with no goal to unwind and nothing to
/// report, so `write/1` on a term nested past the stack killed the host
/// outright. Both renderers now walk an explicit stack, so depth costs heap
/// and not frames, and a term nests as deep as the program made it.
///
/// <para>The error printer keeps its fallback: it renders the culprit of an
/// error, and the stack is not necessarily unwound when it runs, so
/// describing a ball must never need a walk. That one is not about depth and
/// stays.</para></summary>
public sealed class DeepTermRenderingTests
{
    // Deep enough to exhaust any stack the suite might run on, including the
    // 64 MB one the CLIs get.
    private const int Deep = 60_000;

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
    [InlineData("portray_clause(X)")]
    public void WritingATermFarDeeperThanTheStack_JustWritesIt(string goal)
    {
        var e = Engine();
        var text = new StringWriter();
        e.Out = text;
        Assert.True(e.Query($"nest({Deep}, X), {goal}.").Success,
                    $"{goal} did not survive a {Deep}-deep term");
        // ANTI-VACUITY: it wrote the whole thing, not a refusal or an elision.
        Assert.True(text.ToString().Length > Deep,
                    $"only {text.ToString().Length} chars came out");
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
