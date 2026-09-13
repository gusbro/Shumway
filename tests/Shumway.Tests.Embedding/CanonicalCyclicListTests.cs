using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>write_canonical/1 and ignore_ops(true) print a list in FUNCTIONAL
/// notation, and that walk owns the spine itself rather than going through the
/// node gate, so it had neither of the two things that stop a cyclic term:
/// no cycle check, and max_depth does not bound it either (a plain
/// [a,b,c] under max_depth(2) prints in full). A cyclic spine therefore never
/// terminated -- `X = [a|X], write_canonical(X)` wrote `.(a,.(a,` until
/// something gave out.
///
/// <para>The bracket form was always fine: its cycle check is right there in
/// the spine loop. So was a cyclic COMPOUND, which the node gate covers. Only
/// the functional list walk was missing it.</para></summary>
public sealed class CanonicalCyclicListTests
{
    private static string Written(string goal)
    {
        var text = new StringWriter();
        var e = new PrologEngine { Out = text };
        Assert.True(e.Query(goal + ".").Success, goal);
        return text.ToString();
    }

    [Theory]
    [InlineData("X = [a|X], write_canonical(X)", "'.'(a,...)")]
    [InlineData("X = [a,b|X], write_canonical(X)", "'.'(a,'.'(b,...))")]
    // A cycle that closes through another variable is the same cycle.
    [InlineData("X = [a|Y], Y = [b|X], write_canonical(X)", "'.'(a,'.'(b,...))")]
    [InlineData("X = [a|X], write_term(X, [ignore_ops(true)])", ".(a,...)")]
    // max_depth does not bound this walk, so the cycle check may not be
    // conditional on it: with it on, the term still has to terminate.
    [InlineData("X = [a|X], write_term(X, [ignore_ops(true), max_depth(3)])",
                ".(a,...)")]
    public void ACyclicSpineInFunctionalNotationTerminates(string goal, string shown)
    {
        Assert.Equal(shown, Written(goal));
    }

    /// <summary>ANTI-VACUITY: everything acyclic prints exactly as before,
    /// including the max_depth case that deliberately does NOT elide here and
    /// a partial list, whose tail is a variable and not a cycle.</summary>
    [Theory]
    [InlineData("X = [a,b,c], write_canonical(X)", "'.'(a,'.'(b,'.'(c,[])))")]
    [InlineData("X = [a,b,c], write_term(X, [ignore_ops(true)])",
                ".(a,.(b,.(c,[])))")]
    [InlineData("X = [a,b,c], write_term(X, [ignore_ops(true), max_depth(2)])",
                ".(a,.(b,.(c,[])))")]
    [InlineData("X = [], write_canonical(X)", "[]")]
    public void AcyclicListsAreUnchanged(string goal, string shown)
    {
        Assert.Equal(shown, Written(goal));
    }

    [Fact]
    public void APartialListIsNotACycle()
    {
        Assert.StartsWith("'.'(a,_", Written("X = [a|_], write_canonical(X)"));
    }

    /// <summary>The two shapes that were already right, pinned so a change to
    /// the spine walk cannot quietly take them with it.</summary>
    [Fact]
    public void TheBracketFormAndCyclicCompoundsAreUnchanged()
    {
        Assert.Equal("[a|...]", Written("X = [a|X], write(X)"));
        Assert.Equal("f(f(...))", Written("X = f(X), write_term(X, [ignore_ops(true)])"));
        Assert.Equal("f(f(...))", Written("X = f(X), write(X)"));
    }
}
