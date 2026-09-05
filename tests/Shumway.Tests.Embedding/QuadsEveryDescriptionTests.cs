using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An expected block may hold SEVERAL description sentences, and
/// each is a claim of its own: they all describe the same goal, so they all
/// have to hold. Read as one pool of alternatives instead, a transcript
/// claiming three different answers for <c>X = 1</c> passed because the third
/// was right, which is arithmetic no system does.
///
/// <para>The quad text here is synthetic; the published suites live outside
/// the repo.</para></summary>
public sealed class QuadsEveryDescriptionTests
{
    private static string RunQuads(string content)
    {
        var w = new System.IO.StringWriter();
        var e = new PrologEngine { Out = w };
        Assert.True(e.Query("use_module(library(quads)).").Success);
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"quads_ed_{System.Guid.NewGuid():N}.pl");
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
    public void ThreeAnswersForOneGoalIsRefused()
    {
        // The transcript of the issue: one goal, described three times over,
        // two of the three wrong. It used to pass on the strength of the one
        // that was right.
        string report = RunQuads(
            "49\n?- X = 1.\n" +
            "   X = 2.\n" +
            "   X = 1.\n" +
            "   X = 3.\n");
        Assert.Contains("quads: 0/1", report);
        Assert.Contains("failing (1): [49]", report);
    }

    [Fact]
    public void TheReportNamesTheClaimAndNotOnlyTheQuad()
    {
        // With several descriptions, naming the quad does not say which of
        // them the run refutes.
        string report = RunQuads(
            "49\n?- X = 1.\n" +
            "   X = 2.\n" +
            "   X = 1.\n" +
            "   X = 3.\n");
        Assert.Contains("descriptions not met (2):", report);
        Assert.Contains("49: X=2", report);
        Assert.Contains("49: X=3", report);
        Assert.DoesNotContain("49: X=1", report);
    }

    [Fact]
    public void OneDescriptionReportsAsItAlwaysDid()
    {
        // Nothing to disambiguate: the failing id is the whole story, and
        // the extra line would be noise on every failure in a suite.
        string report = RunQuads("7\n?- X = 1.\n      X = 2.\n");
        Assert.Contains("quads: 0/1", report);
        Assert.DoesNotContain("descriptions not met", report);
    }

    [Fact]
    public void DescriptionsThatAllHoldStillPass()
    {
        string report = RunQuads(
            "1\n?- X = 1.\n" +
            "   X = 1.\n" +
            "   true.\n");
        Assert.Contains("quads: 1/1", report);
    }

    [Fact]
    public void AnUnexpectedOnlyDescriptionIsANoteAboutAnotherSystem()
    {
        // A description whose alternatives are all `unexpected` says what
        // some other system does. It is not a claim about this one -- and it
        // may not even be the same experiment: the two descriptions of a
        // reading test differ in what they leave unread.
        string report = RunQuads(
            "40\n?- read(T).\n" +
            "   inputs(\"bar.\"), T = bar, unexpected.\n" +
            "   inputs(\"bar.\"), peeks(\" \"), T = bar.\n");
        Assert.Contains("quads: 1/1", report);
    }

    [Fact]
    public void ButNothingSanctionedAnywhereStillCannotPass()
    {
        // The rule that survives: a block of nothing but wrong answers
        // describes no sanctioned behaviour, so there is nothing to pass.
        string report = RunQuads("42\n?- X = 1.\n      X = 2, unexpected.\n");
        Assert.Contains("quads: 0/1", report);
    }

    [Fact]
    public void EachDescriptionKeepsItsOwnAlternatives()
    {
        // `|` inside one sentence is still a choice: the bar separates
        // sanctioned behaviours, the period separates claims.
        string report = RunQuads(
            "1\n?- X = 1.\n" +
            "   X = 2 | X = 1.\n" +
            "   true | false.\n");
        Assert.Contains("quads: 1/1", report);
    }

    [Fact]
    public void ADescriptionNobodyCouldReadIsStillOnlyReported()
    {
        // Unreadable descriptions were never claims and do not become ones:
        // they are reported, and the sentence that could be read decides.
        string report = RunQuads(
            "1\n?- X = 1.\n" +
            "   X = 1.\n" +
            "   nonsense(indescribable).\n");
        Assert.Contains("quads: 1/1", report);
        Assert.Contains("not understood", report);
    }
}
