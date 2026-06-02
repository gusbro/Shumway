using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 24 chunk 272 — pseudo-random generation (randomize/1,
/// random/1, random_between/3). Per-engine System.Random; seedable
/// via randomize/1 so tests can verify deterministic sequences.
/// </summary>
public class RandomTests
{
    [Fact]
    public void Random1_ReturnsFloatInUnitInterval()
    {
        var e = new PrologEngine();
        for (int i = 0; i < 20; i++)
        {
            var sol = e.Query("random(X).");
            Assert.True(sol.Success);
            double v = ((FloatTerm)sol["X"]!).Value;
            Assert.InRange(v, 0.0, 1.0);
            Assert.True(v < 1.0, "random/1 should be < 1.0");
        }
    }

    [Fact]
    public void RandomBetween_ReturnsIntegerInRangeInclusive()
    {
        var e = new PrologEngine();
        for (int i = 0; i < 50; i++)
        {
            var sol = e.Query("random_between(1, 5, X).");
            Assert.True(sol.Success);
            long v = ((IntTerm)sol["X"]!).Value;
            Assert.InRange(v, 1L, 5L);
        }
    }

    [Fact]
    public void RandomBetween_LowEqualsHigh_AlwaysReturnsThatValue()
    {
        var e = new PrologEngine();
        var sol = e.Query("random_between(42, 42, X).");
        Assert.Equal(42L, ((IntTerm)sol["X"]!).Value);
    }

    [Fact]
    public void RandomBetween_LowGreaterThanHigh_Fails()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("random_between(10, 5, _).").Success);
    }

    [Fact]
    public void Randomize_SameSeed_ProducesSameSequence()
    {
        // After reseeding with the same seed, two engines must produce
        // the same prefix of random/1 outputs.
        double[] Run()
        {
            var e = new PrologEngine();
            e.Query("randomize(12345).");
            var xs = new double[5];
            for (int i = 0; i < xs.Length; i++)
                xs[i] = ((FloatTerm)e.Query("random(X).")["X"]!).Value;
            return xs;
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Randomize_DifferentSeed_ProducesDifferentSequence()
    {
        var e1 = new PrologEngine(); e1.Query("randomize(1).");
        var e2 = new PrologEngine(); e2.Query("randomize(2).");
        double v1 = ((FloatTerm)e1.Query("random(X).")["X"]!).Value;
        double v2 = ((FloatTerm)e2.Query("random(X).")["X"]!).Value;
        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void RandomBetween_LargeRange_DoesntOverflow()
    {
        var e = new PrologEngine();
        var sol = e.Query("random_between(0, 1000000000, X).");
        long v = ((IntTerm)sol["X"]!).Value;
        Assert.InRange(v, 0L, 1000000000L);
    }

    [Fact]
    public void Randomize_NonInteger_TypeError()
    {
        var e = new PrologEngine();
        Assert.Throws<ShumwayPrologException>(
            () => e.Query("randomize(foo)."));
    }

    [Fact]
    public void Randomize_UnboundArg_InstantiationError()
    {
        var e = new PrologEngine();
        Assert.Throws<ShumwayPrologException>(
            () => e.Query("randomize(_)."));
    }
}
