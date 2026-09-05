using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>V ~~ '14.2000'</c>: the answer is a float approximately equal
/// to the decimal written down. The standard's examples say a value is
/// "approximately equal to 14.2000" some twenty times, and a float cannot
/// say it back: the trailing zeroes are the claim, since they say how much of
/// the value is being pinned, and parsing the expectation as a float loses
/// exactly them. So it is written as an atom and read as an interval.
///
/// <para>The quad text here is synthetic; the published suites live outside
/// the repo.</para></summary>
public sealed class QuadsApproximateTests
{
    private static string RunQuads(string content)
    {
        var w = new System.IO.StringWriter();
        var e = new PrologEngine { Out = w };
        Assert.True(e.Query("use_module(library(quads)).").Success);
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"quads_ap_{System.Guid.NewGuid():N}.pl");
        System.IO.File.WriteAllText(path, content);
        try
        {
            Assert.True(e.Query($"consult('{path.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("run_quads.").Success);
            return w.ToString();
        }
        finally { System.IO.File.Delete(path); }
    }

    private static string Quad(string goal, string description)
        => $"1\n?- {goal}.\n      {description}.\n";

    [Fact]
    public void TheExampleFromTheStandard()
    {
        // 9.1.7#3: '+'(0, 3.2+11) evaluates to a value approximately equal
        // to 14.2000 -- which the common double cannot hold exactly.
        Assert.Contains("quads: 1/1",
            RunQuads(Quad("V is 0+(3.2+11)", "V ~~ '14.2000'")));
    }

    [Fact]
    public void TheTrailingZeroesAreTheClaim()
    {
        // 14.2000 pins four decimals, 14.2 pins one: the same value, two
        // different claims, and 14.25 is inside the second and outside the
        // first.
        Assert.Contains("quads: 1/1", RunQuads(Quad("V is 14.24", "V ~~ '14.2'")));
        Assert.Contains("quads: 0/1", RunQuads(Quad("V is 14.24", "V ~~ '14.2000'")));
    }

    [Fact]
    public void AValueOutsideTheIntervalRefutesIt()
        => Assert.Contains("quads: 0/1",
                           RunQuads(Quad("V is 0+(3.2+11)", "V ~~ '14.3000'")));

    [Fact]
    public void TheExponentIsTakenOnTheMantissa()
    {
        Assert.Contains("quads: 1/1",
            RunQuads(Quad("V is 1.0e10 * 1.42", "V ~~ '1.4200e10'")));
        Assert.Contains("quads: 1/1",
            RunQuads(Quad("V is 1.42e-3", "V ~~ '1.4200e-3'")));
        Assert.Contains("quads: 0/1",
            RunQuads(Quad("V is 1.43e-3", "V ~~ '1.4200e-3'")));
    }

    [Fact]
    public void NegativeValuesAreNoDifferent()
    {
        Assert.Contains("quads: 1/1", RunQuads(Quad("V is -3.7", "V ~~ '-3.7000'")));
        Assert.Contains("quads: 0/1", RunQuads(Quad("V is -3.8", "V ~~ '-3.7000'")));
    }

    [Fact]
    public void TheEndsAreTheDECIMALEnds()
    {
        // 14.19995 as a double is a hair BELOW the decimal 14.19995, so it
        // is outside an interval that includes its ends -- which is why the
        // comparison is made against the exact decimal and not against what
        // the bound turns into as a float. Just inside is inside.
        Assert.Contains("quads: 0/1",
            RunQuads(Quad("V is 14.19995", "V ~~ '14.2000'")));
        Assert.Contains("quads: 1/1",
            RunQuads(Quad("V is 14.199951", "V ~~ '14.2000'")));
        Assert.Contains("quads: 0/1",
            RunQuads(Quad("V is 14.20006", "V ~~ '14.2000'")));
    }

    [Fact]
    public void WhatAnsweredHasToBeAFloat()
    {
        // An integer is not approximately anything: it is exact, and a
        // description that meant it would say `V = 14`.
        Assert.Contains("quads: 0/1", RunQuads(Quad("V is 14", "V ~~ '14.0000'")));
    }

    [Fact]
    public void APrecisionFinerThanTheFloatsIsMalformed()
    {
        // Twenty decimals name an interval no two doubles fall on either
        // side of: it describes nothing an implementation could satisfy, so
        // it is reported rather than checked.
        string report = RunQuads(Quad("V is 14.2", "V ~~ '14.20000000000000000001'"));
        Assert.Contains("quads: 0/1", report);
        Assert.Contains("not understood", report);
    }

    [Fact]
    public void SoIsAnythingThatIsNotADecimal()
    {
        foreach (string text in new[] { "not_a_number", "14", "14.", "1.0e" })
        {
            string report = RunQuads(Quad("V is 1.0", $"V ~~ '{text}'"));
            Assert.Contains("not understood", report);
        }
    }

    [Fact]
    public void ItSitsInADescriptionBesideOrdinaryBindings()
    {
        Assert.Contains("quads: 1/1", RunQuads(
            "1\n?- V is 0+(3.2+11), W = ok.\n      V ~~ '14.2000', W = ok.\n"));
        Assert.Contains("quads: 0/1", RunQuads(
            "1\n?- V is 0+(3.2+11), W = ok.\n      V ~~ '14.2000', W = other.\n"));
    }

    [Fact]
    public void AndInAnAnswerSEQUENCE()
    {
        Assert.Contains("quads: 1/1", RunQuads(
            "1\n?- member(V, [1.5, 2.5]).\n      V ~~ '1.5000' ; V ~~ '2.5000'.\n"));
        Assert.Contains("quads: 0/1", RunQuads(
            "1\n?- member(V, [1.5, 2.5]).\n      V ~~ '1.5000' ; V ~~ '2.6000'.\n"));
    }

    [Fact]
    public void ADescriptionCannotSayTwoThingsAboutOneVariable()
    {
        string report = RunQuads(Quad("V is 1.0", "V ~~ '1.0000', V = 2.0"));
        Assert.Contains("not understood", report);
    }

    [Fact]
    public void TheOperatorBelongsToWhoeverImportsTheLibrary()
    {
        // ADR-046: the default table is untouched, so a program that never
        // asked for quads reads `~~` the way it always did.
        var bare = new PrologEngine();
        Assert.False(bare.Query("current_op(_, _, ~~).").Success);

        var quads = new PrologEngine();
        Assert.True(quads.Query("use_module(library(quads)).").Success);
        Assert.True(quads.Query("current_op(700, xfx, ~~).").Success);
    }
}
