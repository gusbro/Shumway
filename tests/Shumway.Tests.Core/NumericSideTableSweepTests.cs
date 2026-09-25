using System.Numerics;
using Shumway.Core;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Core;

/// <summary>ADR-053, applied to the numeric side tables. `BigIntAlloc` and
/// `RationalAlloc` reclaim a slot when backtracking unwinds past the
/// allocation, which covers a SEARCH and covers nothing else: a
/// deterministic loop never backtracks, and that loop is the shape this
/// engine is deployed in. Measured before this: 20,000 transient big
/// integers in a deterministic loop left 20,000 table entries standing
/// after a full collection, linear in the loop, though the program could
/// only ever name one at a time.
///
/// <para>The sweep is the mechanism that covers it, and here it is safer
/// than for the foreign table: a BigInt or Rational id never escapes into a
/// term (every use is an immediate lookup inside Activation), so there is
/// no analogue of the '$foreign'(N) round-trip.</para></summary>
public class NumericSideTableSweepTests(ITestOutputHelper o)
{
    private static void ClearRegisters(Activation e)
    {
        for (int i = 0; i < e.RegisterCount; i++)
            e.SetRegister(i, Cell.Atom(0));
    }

    private static BigInteger Big(int n) => BigInteger.Pow(2, 70) + n;

    /// <summary>THE LEAK: allocations nothing can reach, with no
    /// backtracking to unwind them.
    ///
    /// <para>Asserted on the exact table count rather than on managed
    /// memory. A memory assertion taken after a QUERY measures a dead
    /// activation, whose tables are collectable whatever the engine did,
    /// and an attempt at one read the same 43 MB with the sweep on and
    /// off.</para></summary>
    [Theory]
    [InlineData(100)]
    [InlineData(20_000)]        // flat in the loop, which is the claim
    public void UnreachableBigIntegersAreReleased(int n)
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();                     // something to collect

        for (int i = 0; i < n; i++) e.MakeBigInt(Big(i));     // all dropped
        Assert.Equal(n, e.BigIntTableCount);

        e.CollectHeap();

        o.WriteLine($"bigint table: {n} -> {e.BigIntTableCount}");
        Assert.Equal(0, e.BigIntTableCount);
    }

    /// <summary>THE COUNTER-PROOF: a big integer a register still names must
    /// survive, and still read back as itself. Without the Tag.BigInt case
    /// in the trace this fails RED.</summary>
    [Fact]
    public void AReachableBigIntegerSurvivesAndKeepsItsValue()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();

        for (int i = 0; i < 50; i++) e.MakeBigInt(Big(i));    // dead
        Cell kept = e.MakeBigInt(Big(999));
        e.SetRegister(0, kept);
        for (int i = 0; i < 50; i++) e.MakeBigInt(Big(i));    // dead

        e.CollectHeap();

        Cell r0 = e.GetRegister(0);
        Assert.Equal(Tag.BigInt, r0.Tag);
        Assert.Equal(Big(999), e.AsBigInt(r0));
    }

    /// <summary>One reached only through a compound on the heap: the id has
    /// to be recorded from the TRACE, not only from the roots.</summary>
    [Fact]
    public void ABigIntegerNestedInAStructureSurvives()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();

        int f = e.AllocateHeap(2);
        e.SetHeap(f, Cell.Functor(
            FunctorTable.Intern(AtomTable.Intern("wrap", permanent: true).Id, 1)));
        e.SetHeap(f + 1, e.MakeBigInt(Big(7)));
        e.SetRegister(0, Cell.Str(f));

        e.CollectHeap();

        int nf = e.GetRegister(0).AsHeapIndex;
        Assert.Equal(Big(7), e.AsBigInt(e.GetHeap(nf + 1)));
    }

    /// <summary>A dead entry UNDER a live one is zeroed, not removed: the
    /// live id has to keep meaning what it meant. Zeroing is what releases
    /// the BigInteger's internal magnitude array, which is the memory.
    /// </summary>
    [Fact]
    public void ADeadBigIntegerUnderALiveOneIsZeroedNotRemoved()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();

        e.MakeBigInt(Big(1));                        // id 0, dead
        Cell live = e.MakeBigInt(Big(2));            // id 1, rooted
        e.SetRegister(0, live);
        Assert.Equal(2, e.BigIntTableCount);

        e.CollectHeap();

        Assert.Equal(2, e.BigIntTableCount);
        Cell r0 = e.GetRegister(0);
        Assert.Equal(1, r0.AsBigIntId);              // the live id did NOT move
        Assert.Equal(Big(2), e.AsBigInt(r0));
    }

    /// <summary>The same for rationals (ADR-039), whose struct holds TWO
    /// big integers, so a dead entry retains two magnitude arrays.</summary>
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
        // ANTI-VACUITY: Create REDUCES, and an exact quotient comes back as
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
