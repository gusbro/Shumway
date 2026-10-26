using System.Numerics;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>ADR-064: a big integer is a block of cells on the heap, a RawInt
/// header and its 60-bit limbs, referenced by the BigInt cell's payload. Its
/// lifetime is the heap's: backtracking past the allocation and the collector
/// reclaim it, a reachable one survives a collection with its reference
/// relocated and its limbs contiguous, and a stale reference the conservative
/// scan reads is ignored rather than followed.</summary>
public class BigIntHeapBlockTests
{
    private static void ClearRegisters(Activation e)
    {
        for (int i = 0; i < e.RegisterCount; i++)
            e.SetRegister(i, Cell.Atom(0));
    }

    private static BigInteger Big(int n) => BigInteger.Pow(2, 70) + n;   // two limbs

    [Fact]
    public void MakeBigInt_WritesAHeaderAndItsLimbs()
    {
        var e = new Activation();
        var v = BigInteger.Parse("-123456789012345678901234567890123456789");   // three limbs
        int top0 = e.HeapTop;
        Cell c = e.MakeBigInt(v);

        Assert.Equal(Tag.BigInt, c.Tag);
        Assert.Equal(top0, c.AsHeapIndex);
        Assert.Equal(top0 + 4, e.HeapTop);
        Cell header = e.GetHeap(top0);
        Assert.Equal(Tag.RawInt, header.Tag);
        Assert.Equal(3, Limbs.Count(header));
        Assert.True(Limbs.Negative(header));
        for (int i = 1; i <= 3; i++) Assert.Equal(Tag.RawInt, e.GetHeap(top0 + i).Tag);

        var limbs = e.BigIntLimbs(c, out bool negative);
        Assert.True(negative);
        Assert.Equal(3, limbs.Length);
        Assert.Equal(v, e.AsBigInt(c));
        Assert.Equal(0, e.ExtraTrailTop);   // nothing to trail: the heap top is the record
    }

    [Fact]
    public void MakeBigInt_FromLimbs_CollapsesTheInlineRange()
    {
        var e = new Activation();
        Span<Cell> one = stackalloc Cell[2];

        int n = Limbs.FromLong(5, one);
        Assert.Equal(Cell.Int(5), e.MakeBigInt(one.Slice(0, n), negative: false));
        Assert.Equal(Cell.Int(-5), e.MakeBigInt(one.Slice(0, n), negative: true));

        n = Limbs.FromLong(1L << 59, one);
        Assert.Equal(Cell.Int(Cell.MinInt60), e.MakeBigInt(one.Slice(0, n), negative: true));
        Cell big = e.MakeBigInt(one.Slice(0, n), negative: false);
        Assert.Equal(Tag.BigInt, big.Tag);
        Assert.Equal(BigInteger.Pow(2, 59), e.AsBigInt(big));

        Assert.Equal(Cell.Int(0), e.MakeBigInt(ReadOnlySpan<Cell>.Empty, negative: true));
    }

    [Fact]
    public void MakeBigIntProduct_ComputesIntoTheHeapAndCollapsesTheInlineRange()
    {
        var e = new Activation();
        Span<Cell> one = stackalloc Cell[2];
        int n = Limbs.FromLong(1L << 59, one);
        int top0 = e.HeapTop;
        long cells0 = e.CellsAllocated;

        // 2^59 * 1 negative is the inline minimum: the cells go back.
        Assert.Equal(Cell.Int(Cell.MinInt60), e.MakeBigIntProduct(one.Slice(0, n), 1, negative: true));
        Assert.Equal(top0, e.HeapTop);
        Assert.Equal(cells0, e.CellsAllocated);

        // 2^59 * 3 is two limbs: header plus two, a carry limb produced.
        Cell c = e.MakeBigIntProduct(one.Slice(0, n), 3, negative: false);
        Assert.Equal(Tag.BigInt, c.Tag);
        Assert.Equal(top0 + 3, e.HeapTop);
        Assert.Equal(cells0 + 3, e.CellsAllocated);
        Assert.Equal(BigInteger.Pow(2, 59) * 3, e.AsBigInt(c));

        // A product with no carry gives one reserved cell back.
        Cell d = e.MakeBigIntProduct(e.BigIntLimbs(c, out _), 1, negative: true);
        Assert.Equal(top0 + 6, e.HeapTop);
        Assert.Equal(-(BigInteger.Pow(2, 59) * 3), e.AsBigInt(d));
    }

    [Fact]
    public void BigIntEquals_ComparesSignCountAndLimbs()
    {
        var e = new Activation();
        Cell a = e.MakeBigInt(Big(1));
        Cell b = e.MakeBigInt(Big(1));
        Cell c = e.MakeBigInt(Big(2));
        Cell d = e.MakeBigInt(-Big(1));
        Cell f = e.MakeBigInt(Big(1) * BigInteger.Pow(2, 60));
        Assert.True(e.BigIntEquals(a, a));
        Assert.True(e.BigIntEquals(a, b));
        Assert.False(e.BigIntEquals(a, c));
        Assert.False(e.BigIntEquals(a, d));
        Assert.False(e.BigIntEquals(a, f));
    }

    // Nothing roots the unbound variable these tests allocate first, so a
    // collection leaves exactly the rooted blocks on the heap, from index 0.

    [Theory]
    [InlineData(100)]
    [InlineData(20_000)]
    public void UnreachableBlocksAreReclaimedByACollection(int n)
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();
        int top0 = e.HeapTop;

        for (int i = 0; i < n; i++) e.MakeBigInt(Big(i));
        Assert.Equal(top0 + n * 3, e.HeapTop);

        e.CollectHeap();

        Assert.Equal(0, e.HeapTop);
    }

    [Fact]
    public void AReachableBlockSurvivesRelocatedAndContiguous()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();

        for (int i = 0; i < 50; i++) e.MakeBigInt(Big(i));
        Cell kept = e.MakeBigInt(Big(999));
        e.SetRegister(0, kept);
        for (int i = 0; i < 50; i++) e.MakeBigInt(Big(i));

        e.CollectHeap();

        Cell r0 = e.GetRegister(0);
        Assert.Equal(Tag.BigInt, r0.Tag);
        Assert.True(r0.AsHeapIndex < kept.AsHeapIndex, "the block did not slide down");
        Assert.Equal(0, r0.AsHeapIndex);
        Assert.Equal(3, e.HeapTop);
        Assert.Equal(Big(999), e.AsBigInt(r0));
        Assert.Equal(2, Limbs.Count(e.GetHeap(r0.AsHeapIndex)));
    }

    [Fact]
    public void ABlockNestedInAStructureSurvives()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();

        for (int i = 0; i < 20; i++) e.MakeBigInt(Big(i));
        int f = e.AllocateHeap(2);
        e.SetHeap(f, Cell.Functor(
            FunctorTable.Intern(AtomTable.Intern("wrap", permanent: true).Id, 1)));
        e.SetHeap(f + 1, e.MakeBigInt(Big(7)));
        e.SetRegister(0, Cell.Str(f));

        e.CollectHeap();

        int nf = e.GetRegister(0).AsHeapIndex;
        Assert.Equal(Big(7), e.AsBigInt(e.GetHeap(nf + 1)));
    }

    [Fact]
    public void TwoReferencesToOneBlockStayOneBlock()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();

        Cell c = e.MakeBigInt(Big(3));
        e.SetRegister(0, c);
        e.SetRegister(1, c);

        e.CollectHeap();

        Assert.Equal(e.GetRegister(0), e.GetRegister(1));
        Assert.Equal(3, e.HeapTop);
        Assert.Equal(Big(3), e.AsBigInt(e.GetRegister(1)));
    }

    [Fact]
    public void AStaleReferencePastTheTopIsIgnored()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();
        e.SetRegister(0, Cell.BigInt(123_456_789));

        e.CollectHeap();   // must neither throw nor retain anything
        Assert.Equal(0, e.HeapTop);
    }

    [Fact]
    public void AStaleReferenceToANonHeaderIsIgnored()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();
        int i = e.AllocateHeap(1);
        e.SetHeap(i, Cell.Int(42));
        e.SetRegister(0, Cell.BigInt(i));

        e.CollectHeap();
        Assert.Equal(0, e.HeapTop);   // the Int cell was not retained as a block
    }

    [Fact]
    public void AHeaderClaimingLimbsPastTheTopIsIgnored()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();
        int h = e.AllocateHeap(2);
        e.SetHeap(h, Limbs.Header(1_000_000, negative: false));
        e.SetHeap(h + 1, Limbs.Make(1));
        e.SetRegister(0, Cell.BigInt(h));

        e.CollectHeap();
        Assert.Equal(0, e.HeapTop);
    }

    [Fact]
    public void BacktrackingReclaimsABlockAboveTheChoicePoint()
    {
        var e = new Activation();
        e.SetHbForTesting(e.HeapTop);
        e.PushChoicePoint(0, 1);
        int top = e.HeapTop;
        int extra = e.ExtraTrailTop;

        Cell c = e.MakeBigInt(Big(5));
        Assert.Equal(Tag.BigInt, c.Tag);
        Assert.Equal(top + 3, e.HeapTop);
        Assert.Equal(extra, e.ExtraTrailTop);

        e.RetryMeElse(0);
        Assert.Equal(top, e.HeapTop);
    }
}
