using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An answer may still stand on a CONSTRAINT, and a transcript says
/// so with <c>maybe</c>: `dif(X,Y), X = a` answers `X = a, maybe`, not
/// `X = a`. Its absence is a claim too — an answer written without it stands
/// on its own — which is what makes `X = a` a wrong description of that
/// goal and `X = a, maybe` a wrong description of `X = a`.
///
/// <para>Also pins the transcript vocabulary that already worked, so it
/// cannot regress silently: a thrown ball described as <c>throw/1</c>,
/// <c>peeks/1</c> beside <c>inputs/1</c>, and <c>outputs/1</c> read as a DCG
/// body (text pieces, <c>[_]</c> for one arbitrary character, <c>...</c> for
/// a part not written down).</para></summary>
public sealed class QuadsPendingConstraintTests
{
    private static string RunQuads(string content)
    {
        var w = new System.IO.StringWriter();
        var e = new PrologEngine { Out = w };
        Assert.True(e.Query("use_module(library(quads)).").Success);
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"quads_pc_{System.Guid.NewGuid():N}.pl");
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
    public void TheIssueTranscript_Passes()
    {
        // Verbatim: a constrained answer, the three ways of describing it
        // wrongly, and the one that is right.
        string report = RunQuads(
            "37\n?- dif(X,Y), X = a.\n" +
            "   true, unexpected.\n" +
            "   X = a, unexpected. % not enough\n" +
            "   X = a, maybe.\n" +
            "   maybe, unexpected.  % answer substitutions still relevant\n");
        Assert.Contains("quads: 1/1", report);
        Assert.DoesNotContain("not understood", report);
    }

    [Fact]
    public void AConstrainedAnswerDescribedWithoutMaybe_Fails()
    {
        string report = RunQuads(
            "t\n?- dif(X,Y), X = a.\n   X = a.\n");
        Assert.Contains("quads: 0/1", report);
    }

    [Fact]
    public void AnUnconstrainedAnswerDescribedWithMaybe_Fails()
    {
        string report = RunQuads("t\n?- X = a.\n   X = a, maybe.\n");
        Assert.Contains("quads: 0/1", report);
    }

    [Fact]
    public void EachSideDescribedRightly_Passes()
    {
        Assert.Contains("quads: 1/1",
            RunQuads("t\n?- dif(X,Y), X = a.\n   X = a, maybe.\n"));
        Assert.Contains("quads: 1/1",
            RunQuads("t\n?- X = a.\n   X = a.\n"));
    }

    // ---- vocabulary that already worked ----

    [Fact]
    public void AThrownBall_IsDescribedByThrow()
    {
        string report = RunQuads("t\n?- throw(stopped).\n   throw(stopped).\n");
        Assert.Contains("quads: 1/1", report);
        Assert.DoesNotContain("not understood", report);
    }

    [Fact]
    public void PeeksSaysWhatTheGoalMustLeaveUnread()
    {
        // Reading `1.` off `1.` alone cannot tell that the number ended;
        // the peek is how the transcript says it did.
        string report = RunQuads(
            "29\n?- read(X).\n" +
            "   inputs(\"1.\"), X = 1, unexpected. % could be 1..2\n" +
            "   inputs(\"1.\"), peeks(\" \"), X = 1.\n" +
            "   inputs(\"1. \"), peeks(\" \"), X = 1, unexpected.\n");
        Assert.Contains("quads: 1/1", report);
        Assert.DoesNotContain("not understood", report);
    }

    [Fact]
    public void OutputsReadsAsADcgBody()
    {
        string report = RunQuads(
            "22\n?- write('_A').\n" +
            "   outputs(\"_A\").\n" +
            "   outputs(\"_\"), unexpected.\n" +
            "   outputs((\"_\",\"A\")).\n" +
            "   outputs((\"_\",...,\"A\")).\n" +
            "   outputs((\"_\",...,\"B\")), unexpected.\n" +
            "   outputs((\"_\",[_],[_])), unexpected.\n");
        Assert.Contains("quads: 1/1", report);
        Assert.DoesNotContain("not understood", report);
    }

    // ---- `| other_answer_sequence`: the order is not the claim ----

    [Fact]
    public void TheSetofTranscript_Passes()
    {
        // 8.10.3.4#7: setof/3 enumerates its free variables in whatever
        // order a system likes, so a transcript naming one order would be
        // claiming that order. The marker says the others are equally
        // sanctioned.
        string report = RunQuads(
            "7\n?- setof(1, (Y=2 ; Y=1), L).\n" +
            "   Y = 1, L = [1]\n;  Y = 2, L = [1]\n|  other_answer_sequence.\n");
        Assert.Contains("quads: 1/1", report);
        Assert.DoesNotContain("not understood", report);
    }

    [Fact]
    public void TheOtherOrderIsSanctionedToo()
    {
        // Written the other way round from what this engine answers — which
        // is the whole point, and what the marker has to make pass.
        Assert.Contains("quads: 1/1", RunQuads(
            "t\n?- setof(1, (Y=2 ; Y=1), L).\n" +
            "   Y = 2, L = [1]\n;  Y = 1, L = [1]\n|  other_answer_sequence.\n"));
        // Without the marker the order IS claimed, and this one is wrong.
        Assert.Contains("quads: 0/1", RunQuads(
            "t\n?- setof(1, (Y=2 ; Y=1), L).\n" +
            "   Y = 2, L = [1]\n;  Y = 1, L = [1].\n"));
    }

    [Fact]
    public void ItFreesTheOrder_NotTheAnswers()
    {
        // A different answer is still a different answer...
        Assert.Contains("quads: 0/1", RunQuads(
            "t\n?- setof(1, (Y=2 ; Y=1), L).\n" +
            "   Y = 2, L = [1]\n;  Y = 3, L = [1]\n|  other_answer_sequence.\n"));
        // ...and a closed sequence still claims there are no others.
        Assert.Contains("quads: 0/1", RunQuads(
            "t\n?- setof(1, (Y=2 ; Y=1), L).\n" +
            "   Y = 1, L = [1]\n|  other_answer_sequence.\n"));
    }

    [Fact]
    public void TheBarSeparatesAlternatives_AboveTheAnswerSeparator()
    {
        // `A ; B | C` is the sequence `A ; B` and the alternative `C`. With
        // the bar at the same priority as `;` it bound tighter and swallowed
        // the last answer, which is how the transcript above was read as one
        // unparsable block.
        string report = RunQuads(
            "t\n?- member(X, [a,b]).\n" +
            "   X = a\n;  X = b\n|  false.\n");
        Assert.Contains("quads: 1/1", report);
        Assert.DoesNotContain("not understood", report);
    }

    [Fact]
    public void ADcgListStandsForArbitraryCharacters()
    {
        // `[_]` is one character, whatever it is — and counting them is the
        // check: two of them do not match a two-character output preceded by
        // a piece.
        Assert.Contains("quads: 1/1",
            RunQuads("t\n?- write('_A').\n   outputs((\"_\",[_])).\n"));
        Assert.Contains("quads: 1/1",
            RunQuads("t\n?- write('_A').\n   outputs([_,_]).\n"));
        Assert.Contains("quads: 0/1",
            RunQuads("t\n?- write('_A').\n   outputs((\"_\",[_],[_])).\n"));
    }
}
