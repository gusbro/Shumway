using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An answer sequence ending <c>, unexpected</c> is a NEGATIVE
/// transcript: it documents the answers a buggy system gives — the ones
/// before the marker in order, then the marked one — and holds of a system
/// that does NOT reproduce it. The published suites use it to pin known-bad
/// continuations (`X = a ; X = c, unexpected` after `member(X,"abc")`);
/// these blocks used to be dropped as "not understood", so they checked
/// nothing.</summary>
public sealed class QuadsUnexpectedAnswerTests
{
    private static string RunQuads(string content)
    {
        var w = new System.IO.StringWriter();
        var e = new PrologEngine { Out = w };
        Assert.True(e.Query("use_module(library(quads)).").Success);
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"quads_ux_{System.Guid.NewGuid():N}.pl");
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
    public void TheIssueShapes_ParseAndPass()
    {
        // The two quads of the report, verbatim: a full enumeration
        // sanctions the behaviour, and the unexpected-sequence documents a
        // continuation this system does not produce.
        string report = RunQuads(
            "40\n?- member(X,\"abc\").\n" +
            "   X = a\n;  X = b\n;  X = c.\n" +
            "   X = a\n;  X = c, unexpected.\n" +
            "\n41\n?- member(X, Xs).\n" +
            "   Xs = [X|_A]\n;  ... .\n" +
            "   Xs = [X|_A]\n;  Xs = [_A,X|_B]\n;  Xs = [_A,_B,X|_C]\n;  ... .\n" +
            "   Xs = [X|_A]\n;  Xs = [], unexpected.\n");
        Assert.Contains("quads: 2/2", report);
        Assert.DoesNotContain("not understood", report);
    }

    [Fact]
    public void ReproducingTheDocumentedBug_Fails()
    {
        // Our second answer IS b — the transcript `X = a ; X = b,
        // unexpected` is reproduced exactly, so the quad must fail and the
        // report must name the refuted claim.
        string report = RunQuads(
            "t\n?- member(X,[a,b,c]).\n" +
            "   X = a\n;  X = b\n;  X = c.\n" +
            "   X = a\n;  X = b, unexpected.\n");
        Assert.Contains("quads: 0/1", report);
        Assert.Contains("unexpected", report);
    }

    [Fact]
    public void ADifferentAnswerAtTheMarkedPoint_Holds()
    {
        string report = RunQuads(
            "t\n?- member(X,[a,b,c]).\n" +
            "   X = a\n;  X = b\n;  X = c.\n" +
            "   X = a\n;  X = z, unexpected.\n");
        Assert.Contains("quads: 1/1", report);
    }

    [Fact]
    public void AMismatchedPrefix_MeansTheTranscriptIsNotReproduced()
    {
        string report = RunQuads(
            "t\n?- member(X,[a,b]).\n" +
            "   X = a\n;  X = b.\n" +
            "   X = q\n;  X = b, unexpected.\n");
        Assert.Contains("quads: 1/1", report);
    }

    [Fact]
    public void NoFurtherAnswerAtTheMarkedPoint_Holds()
    {
        // The surprise sits one past the last real answer: nothing arrives
        // there, so the bug is absent.
        string report = RunQuads(
            "t\n?- member(X,[a]).\n" +
            "   X = a.\n" +
            "   X = a\n;  X = b, unexpected.\n");
        Assert.Contains("quads: 1/1", report);
    }

    [Fact]
    public void ANegativeClaimAlone_SanctionsNothing()
    {
        // A quad whose only description is the wrong continuation says
        // nothing a conforming system could match — it can only fail, like
        // any block of nothing but unexpected behaviour.
        string report = RunQuads(
            "t\n?- member(X,[a,b]).\n" +
            "   X = a\n;  X = z, unexpected.\n");
        Assert.Contains("quads: 0/1", report);
    }
}
