using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What a transcript can say about INPUT, and how the harness makes
/// it observable.
///
/// <para>A description names the text the goal reads (<c>inputs/1</c>), the
/// text it must leave unread (<c>peeks/1</c>), or that it reaches for input
/// at all (<c>waits</c>). What ties them together is a sentinel byte written
/// behind the text — one no character can be read from — so a goal that
/// reads PAST what the description sanctioned raises
/// <c>representation_error(character)</c> instead of quietly seeing
/// end_of_file. Without it a claim like <c>inputs("a"), C = a</c> could not
/// tell a goal that read one character from one that read one and looked at
/// what followed, and <c>waits</c> could not hold for <c>peek_char/1</c>,
/// which reaches for input while consuming nothing.</para></summary>
public sealed class QuadsInputTests
{
    private static string RunQuads(string content)
    {
        var w = new System.IO.StringWriter();
        var e = new PrologEngine { Out = w };
        Assert.True(e.Query("use_module(library(quads)).").Success);
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"quads_in_{System.Guid.NewGuid():N}.pl");
        System.IO.File.WriteAllText(path, content);
        try
        {
            Assert.True(e.Query($"consult('{path.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("run_quads.").Success);
            return w.ToString();
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void TheGetCharTranscript_Passes()
    {
        string report = RunQuads(
            "a\n?- get_char(C).\n" +
            "   waits.\n" +
            "   inputs(\"a\"), C = a.\n" +
            "   inputs(\"b\"), C = b.\n");
        Assert.Contains("quads: 1/1", report);
        Assert.DoesNotContain("not understood", report);
    }

    [Fact]
    public void PeekingIsWaiting_AndPeeksAloneIsAnInput()
    {
        // peek_char/1 consumes nothing, so "did it consume the character?"
        // could never see it waiting; reaching for the sentinel can. And a
        // description whose only text is the peek still has to open a
        // stream — the peek text IS the input there.
        string report = RunQuads(
            "b\n?- peek_char(C).\n" +
            "   waits.\n" +
            "   peeks(\"a\"), C = a.\n");
        Assert.Contains("quads: 1/1", report);
        Assert.DoesNotContain("not understood", report);
    }

    [Fact]
    public void ReadingPastTheSanctionedInput_IsNotSanctioned()
    {
        // The goal reads its character and then looks at what follows. The
        // description says nothing about a look, so it is not met — before
        // the sentinel this passed, with end_of_file standing in for the
        // character that was never described.
        string report = RunQuads(
            "c\n?- get_char(C), peek_char(D).\n" +
            "   inputs(\"a\"), C = a, D = end_of_file.\n");
        Assert.Contains("quads: 0/1", report);
    }

    [Fact]
    public void AGoalThatDoesNotRead_DoesNotWait()
    {
        // The other side of the probe: nothing reaches for input, so
        // `waits` is false and the quad fails.
        Assert.Contains("quads: 0/1", RunQuads("t\n?- true.\n   waits.\n"));
    }

    [Fact]
    public void ThePeekIsWhatSaysWhereTheTermEnded()
    {
        // A reader has to look past the end token to know it ended; the
        // peek is how the transcript says the look happened and what it
        // found. Without it, `inputs("1.")` alone does not sanction the
        // look — which is the point of the marked description.
        string report = RunQuads(
            "29\n?- read(X).\n" +
            "   inputs(\"1.\"), X = 1, unexpected. % not enough information\n" +
            "   inputs(\"1.\"), peeks(\" \"), X = 1.\n" +
            "   inputs(\"1. \"), peeks(\" \"), X = 1, unexpected.\n");
        Assert.Contains("quads: 1/1", report);
        Assert.DoesNotContain("not understood", report);
    }

    [Fact]
    public void AnOutputClaimMustBeAText()
    {
        // `outputs(_)` claims nothing: a variable is not a text pattern, and
        // taking it for one made it match whatever the goal wrote — which
        // for a goal that never stops writing meant capturing all of it
        // (issue #112). Reported, and its goal never runs.
        string report = RunQuads(
            """
            imp ?- repeat, write('hello '), fail.
               outputs("hello "),
               outputs("hello "),
               outputs(_),
               loops.

            """);
        Assert.Contains("quads: 0/1", report);
        Assert.Contains("not understood", report);
        Assert.DoesNotContain("hello hello", report);
    }

    [Fact]
    public void ALoopingGoalsOutputStaysInTheHarness()
    {
        // A quad is a test being run: its goal's output belongs to the
        // harness, not to whoever is reading the report. Bounded, so a goal
        // writing without end is refused rather than accumulated — and
        // writing without end is not terminating, so `loops` holds.
        string report = RunQuads(
            """
            t ?- repeat, write(x), fail.
               loops.

            """);
        Assert.Contains("quads: 1/1", report);
        Assert.DoesNotContain("xxx", report);
    }

    [Fact]
    public void WhatTheGoalLeaves_IsComparedToThePeek()
    {
        // inputs ++ peeks IS the text on the stream, so the peek is there
        // unless the goal EATS into it: one that consumes both characters
        // leaves nothing where the description says a character remains.
        Assert.Contains("quads: 1/1", RunQuads(
            "t\n?- get_char(C).\n   inputs(\"a\"), peeks(\"b\"), C = a.\n"));
        Assert.Contains("quads: 0/1", RunQuads(
            "t\n?- get_char(C), get_char(_).\n   inputs(\"a\"), peeks(\"b\"), C = a.\n"));
    }
}
