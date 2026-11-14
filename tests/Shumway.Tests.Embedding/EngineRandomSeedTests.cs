using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Every engine draws a random sequence of its own. .NET Framework
/// seeds a Random made without a seed from the tick count, so two engines
/// whose generators were made in the same tick drew the same numbers.
/// library(quads) names its scratch input with one: two engines running
/// quads at once in one process (the test suite does) wrote, read and
/// deleted the same file.</summary>
public class EngineRandomSeedTests
{
    private static string Draws(PrologEngine e)
    {
        var s = e.Query("findall(R, (between(1, 8, _), random_between(0, 1000000000, R)), L), "
            + "term_to_atom(L, A).");
        Assert.True(s.Success);
        return s["A"]!.ToString()!;
    }

    [Fact]
    public void TwoEnginesMadeTogetherDrawDifferentSequences()
    {
        var a = new PrologEngine();
        var b = new PrologEngine();
        // Both generators made back to back, well inside one tick.
        _ = a.Random;
        _ = b.Random;
        Assert.NotEqual(Draws(a), Draws(b));
    }

    [Fact]
    public void ASeedStillGivesItsSequence()
    {
        var a = new PrologEngine();
        var b = new PrologEngine();
        a.Randomize(42);
        b.Randomize(42);
        Assert.Equal(Draws(a), Draws(b));
    }
}
