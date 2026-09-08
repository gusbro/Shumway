using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Successive <c>outputs/1</c> claims continue one another: a goal
/// that writes "hello " twice may be transcribed either as one claim or as
/// two, and both say the same thing. The second used to OVERWRITE the first,
/// so the two-claim form ran the goal to its limit and then failed while the
/// one-claim form passed.
///
/// <para>The quad text here is synthetic; the published suites live outside
/// the repo.</para></summary>
public sealed class QuadsSuccessiveOutputsTests
{
    private static string RunQuads(string content)
    {
        var w = new System.IO.StringWriter();
        var e = new PrologEngine { Out = w };
        Assert.True(e.Query("use_module(library(quads)).").Success);
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"quads_so_{System.Guid.NewGuid():N}.pl");
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
    public void TwoOutputClaimsSayWhatOneJoinedClaimSays()
    {
        // The issue's pair, verbatim in shape: the same goal and the same
        // output, written down both ways. Both must hold.
        string report = RunQuads(
            "imp2\n?- ( ( true ; true ), write('hello ') ; repeat ), fail.\n" +
            "   outputs(\"hello \"),\n   outputs(\"hello \"),\n   loops.\n\n" +
            "imp3\n?- ( ( true ; true ), write('hello ') ; repeat ), fail.\n" +
            "   outputs(\"hello hello \"),\n   loops.\n");
        Assert.Contains("quads: 2/2", report);
    }

    [Fact]
    public void JoinedClaimsStillCompareTheWholeText()
    {
        // Continuation is not permission: the pieces are matched in order
        // and the text still has to be what the goal writes.
        string report = RunQuads(
            "w1\n?- write('hello ').\n" +
            "   outputs(\"hello \"),\n   outputs(\"there\"),\n   true.\n");
        Assert.Contains("quads: 0/1", report);
        Assert.Contains("[w1]", report);
    }

    [Fact]
    public void AnUnreadableClaimAmongThemIsStillReported()
    {
        // A claim that is not a text pattern reaches the report instead of
        // being run (issue #112) — joining must not swallow it.
        string report = RunQuads(
            "b1\n?- ( repeat, write(x), fail ; true ).\n" +
            "   outputs(\"x\"),\n   outputs(_),\n   loops.\n");
        Assert.Contains("not understood", report);
        Assert.Contains("b1", report);
    }
}
