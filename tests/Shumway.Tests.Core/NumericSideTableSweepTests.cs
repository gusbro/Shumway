using System.Numerics;
using Shumway.Core;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Core;

/// <summary>ADR-053, applied to the rational side table. `RationalAlloc`
/// reclaims a slot when backtracking unwinds past the allocation, which
/// covers a search and covers nothing else: a deterministic loop never
/// backtracks, and that loop is the shape this engine is deployed in. The
/// sweep is the mechanism that covers it, and here it is safer than for the
/// foreign table: a Rational id never escapes into a term (every use is an
/// immediate lookup inside Activation), so there is no analogue of the
/// '$foreign'(N) round-trip.
///
/// <para>Big integers left the side tables with ADR-064; their lifetime is
/// the heap's, tested in <see cref="BigIntHeapBlockTests"/>.</para></summary>
public class NumericSideTableSweepTests(ITestOutputHelper o)
{
    private static void ClearRegisters(Activation e)
    {
        for (int i = 0; i < e.RegisterCount; i++)
            e.SetRegister(i, Cell.Atom(0));
    }

    private static BigInteger Big(int n) => BigInteger.Pow(2, 70) + n;

    /// <summary>A rational's struct holds two big integers, so a dead entry
    /// retains two magnitude arrays; the sweep releases them and the one a
    /// register still names survives with its value.</summary>
    [Fact]
    public void UnreachableRationalsAreReleasedAndAReachableOneSurvives()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();

        for (int i = 0; i < 50; i++)
            e.MakeRational(Rational.Create(Big(i), 3));
        var keptValue = Rational.Create(Big(999), 3);
        Cell kept = e.MakeRational(keptValue);
        // Anti-vacuity: Create reduces, and an exact quotient comes back as
        // an integer cell that never touches this table at all. 7 divided
        // Big(999) exactly and made the first draft of this test assert
        // nothing.
        Assert.Equal(Tag.Rational, kept.Tag);
        e.SetRegister(0, kept);
        for (int i = 0; i < 50; i++)
            e.MakeRational(Rational.Create(Big(i), 3));
        int before = e.RationalTableCount;

        e.CollectHeap();

        o.WriteLine($"rational table: {before} -> {e.RationalTableCount}");
        Assert.True(e.RationalTableCount < before,
            "no rational slot was reclaimed");
        Cell r0 = e.GetRegister(0);
        Assert.Equal(Tag.Rational, r0.Tag);
        Assert.Equal(keptValue, e.AsRational(r0));
    }
}
