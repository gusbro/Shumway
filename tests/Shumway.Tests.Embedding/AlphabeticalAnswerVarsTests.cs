using System.IO;
using System.Text.RegularExpressions;
using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Issue #120 follow-up: the top level names an answer's unbound
/// variables the way Scryer does — <c>_A, _B, …, _Z, _A1, …</c> in order of
/// appearance — instead of the engine's heap-address spelling (<c>_G10</c>,
/// with gaps). A name a user variable already holds is skipped, and no bare
/// <c>_</c> is ever used, so a variable shown once is still named. These are
/// the exact queries from Neumerkel's report, and the answers now match what
/// GNU/SWI/Scryer print.</summary>
public sealed class AlphabeticalAnswerVarsTests
{
    private static string Answer(string query)
    {
        var e = new PrologEngine { Out = new StringWriter() };
        using var run = new TopLevelSession(e).StartQuery(query);
        Assert.True(run.MoveNext(), $"no solution for {query}");
        return run.Format(200);
    }

    [Fact]
    public void UnboundVariablesAreNamedAlphabeticallyInOrder()
    {
        Assert.Equal("L = [_A, _B, _C],\nT = f(_A, _B, _C)",
            Answer("length(L,3),T=..[f|L]."));
    }

    /// <summary>A generated name that a user variable already holds is skipped:
    /// the user named <c>_B</c>, so the sequence goes <c>_A, _C, _D</c> — the
    /// exact output GNU and SWI give.</summary>
    [Fact]
    public void AGeneratedNameCollidingWithAUserVariableIsSkipped()
    {
        Assert.Equal("L = [_A, _C, _D],\nT = f(_A, _C, _D)",
            Answer("length(L,3),T=..[f|L],_B=3."));
    }

    /// <summary>Never a bare <c>_</c>: a one-element list of an unbound variable
    /// is <c>[_A]</c>, not <c>[_]</c> (SWI's problematic collapse Neumerkel
    /// objected to).</summary>
    [Fact]
    public void ASingletonUnboundVariableIsNamedNotAnonymous()
    {
        string a = Answer("length(L,1),L=K.");
        Assert.Equal("L = K,\nK = [_A]", a);
        Assert.DoesNotContain("[_]", a);
    }

    /// <summary>No engine heap-address spelling ever reaches the answer, and
    /// every generated name is the alphabetical shape.</summary>
    [Fact]
    public void NoEngineHeapNamesInTheAnswer()
    {
        string a = Answer("length(L,5),T=..[g|L].");
        Assert.DoesNotContain("_G", a);
        foreach (Match m in Regex.Matches(a, @"_[A-Za-z]\w*"))
            Assert.Matches(@"^_[A-Z]\d*$", m.Value);
    }

    /// <summary>Past <c>_Z</c> the sequence continues <c>_A1, _B1, …</c>, not
    /// <c>_AA</c> — the Scryer convention.</summary>
    [Fact]
    public void PastZTheSequenceWrapsWithADigitSuffix()
    {
        string a = Answer("length(L,28).");
        Assert.Contains("_Z,", a);
        Assert.Contains("_A1,", a);
        Assert.Contains("_B1]", a);
        Assert.DoesNotContain("_AA", a);
    }
}
