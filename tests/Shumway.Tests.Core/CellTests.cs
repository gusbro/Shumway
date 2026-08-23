using System.Runtime.CompilerServices;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class CellTests
{
    // ---------- Layout ----------

    [Fact]
    public void Cell_SizeIs8Bytes()
    {
        Assert.Equal(8, Unsafe.SizeOf<Cell>());
    }

    [Fact]
    public void Tag_ValuesMatchAdr002Specification()
    {
        Assert.Equal(0x0, (byte)Tag.Ref);
        Assert.Equal(0x1, (byte)Tag.Str);
        Assert.Equal(0x2, (byte)Tag.Lis);
        Assert.Equal(0x3, (byte)Tag.Functor);
        Assert.Equal(0x4, (byte)Tag.Atom);
        Assert.Equal(0x5, (byte)Tag.Int);
        Assert.Equal(0x6, (byte)Tag.Float);
        Assert.Equal(0x7, (byte)Tag.BigInt);
        Assert.Equal(0x9, (byte)Tag.Foreign);
        Assert.Equal(0xA, (byte)Tag.AttVar);
        Assert.Equal(0xB, (byte)Tag.Pstr);
        Assert.Equal(0xC, (byte)Tag.PstrBuffer);
    }

    // ---------- Hex patterns from docs/design/cell-layout-detail.md ----------

    [Theory]
    [InlineData(0, 0x0000_0000_0000_0000L)]      // unbound var at heap[0]
    [InlineData(100, 0x0000_0000_0000_0064L)]    // unbound var at heap[100]
    public void Ref_MatchesDocumentedHexPattern(int heapIdx, long expectedData)
    {
        var c = Cell.Ref(heapIdx);
        Assert.Equal(expectedData, c.Data);
        Assert.Equal(Tag.Ref, c.Tag);
        Assert.Equal(heapIdx, c.AsHeapIndex);
    }

    [Theory]
    [InlineData(0, 0x4000_0000_0000_0000L)]      // [] atom (id=0)
    [InlineData(3, 0x4000_0000_0000_0003L)]      // true atom (id=3)
    public void Atom_MatchesDocumentedHexPattern(int atomId, long expectedData)
    {
        var c = Cell.Atom(atomId);
        Assert.Equal(expectedData, c.Data);
        Assert.Equal(Tag.Atom, c.Tag);
        Assert.Equal(atomId, c.AsAtomId);
    }

    [Theory]
    [InlineData(0L, 0x5000_0000_0000_0000L)]
    [InlineData(42L, 0x5000_0000_0000_002AL)]
    [InlineData(-1L, unchecked((long)0x5FFF_FFFF_FFFF_FFFFUL))]
    public void Int_MatchesDocumentedHexPattern(long value, long expectedData)
    {
        var c = Cell.Int(value);
        Assert.Equal(expectedData, c.Data);
        Assert.Equal(Tag.Int, c.Tag);
        Assert.Equal(value, c.AsInt);
    }

    [Fact]
    public void Str_MatchesDocumentedHexPattern()
    {
        // STR pointing to heap[10] → 0x1000_0000_0000_000A
        var c = Cell.Str(10);
        Assert.Equal(0x1000_0000_0000_000AL, c.Data);
        Assert.Equal(Tag.Str, c.Tag);
        Assert.Equal(10, c.AsHeapIndex);
    }

    [Fact]
    public void Lis_MatchesDocumentedHexPattern()
    {
        // LIS pointing to heap[20] → 0x2000_0000_0000_0014
        var c = Cell.Lis(20);
        Assert.Equal(0x2000_0000_0000_0014L, c.Data);
        Assert.Equal(Tag.Lis, c.Tag);
        Assert.Equal(20, c.AsHeapIndex);
    }

    [Fact]
    public void Functor_MatchesDocumentedHexPattern()
    {
        // FUNCTOR id=5 → 0x3000_0000_0000_0005
        var c = Cell.Functor(5);
        Assert.Equal(0x3000_0000_0000_0005L, c.Data);
        Assert.Equal(Tag.Functor, c.Tag);
        Assert.Equal(5, c.AsFunctorId);
    }

    // ---------- Round trips ----------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void Ref_RoundTrips(int heapIdx)
    {
        var c = Cell.Ref(heapIdx);
        Assert.Equal(Tag.Ref, c.Tag);
        Assert.Equal(heapIdx, c.AsHeapIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void UnboundVar_PointsToItself(int selfHeapIdx)
    {
        var c = Cell.UnboundVar(selfHeapIdx);
        Assert.Equal(Tag.Ref, c.Tag);
        Assert.Equal(selfHeapIdx, c.AsHeapIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(int.MaxValue)]
    public void Atom_RoundTrips(int atomId)
    {
        var c = Cell.Atom(atomId);
        Assert.Equal(Tag.Atom, c.Tag);
        Assert.Equal(atomId, c.AsAtomId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void Functor_RoundTrips(int functorId)
    {
        var c = Cell.Functor(functorId);
        Assert.Equal(Tag.Functor, c.Tag);
        Assert.Equal(functorId, c.AsFunctorId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(123)]
    public void BigInt_RoundTrips(int tableId)
    {
        var c = Cell.BigInt(tableId);
        Assert.Equal(Tag.BigInt, c.Tag);
        Assert.Equal(tableId, c.AsBigIntId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(789)]
    public void Foreign_RoundTrips(int tableId)
    {
        var c = Cell.Foreign(tableId);
        Assert.Equal(Tag.Foreign, c.Tag);
        Assert.Equal(tableId, c.AsForeignId);
    }

    // ---------- Int sign extension at boundaries ----------

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(42L)]
    [InlineData(-42L)]
    [InlineData(Cell.MaxInt60)]
    [InlineData(Cell.MinInt60)]
    [InlineData(Cell.MaxInt60 - 1)]
    [InlineData(Cell.MinInt60 + 1)]
    public void Int_PreservesSignedValue(long value)
    {
        var c = Cell.Int(value);
        Assert.Equal(Tag.Int, c.Tag);
        Assert.Equal(value, c.AsInt);
    }

    [Fact]
    public void Int_AboveMaxInt60_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cell.Int(Cell.MaxInt60 + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Cell.Int(long.MaxValue));
    }

    [Fact]
    public void Int_BelowMinInt60_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cell.Int(Cell.MinInt60 - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Cell.Int(long.MinValue));
    }

    [Fact]
    public void Int60_Constants_MatchSpec()
    {
        Assert.Equal(-(1L << 59), Cell.MinInt60);
        Assert.Equal((1L << 59) - 1, Cell.MaxInt60);
    }

    // ---------- Float ----------

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(3.141592653589793)]
    [InlineData(2.718281828459045)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    [InlineData(double.Epsilon)]
    public void Float_RoundTripsBitExact(double value)
    {
        var (header, paired) = Cell.MakeFloat(value, pairedHeapIdx: 42);

        Assert.Equal(Tag.Float, header.Tag);
        Assert.Equal(Tag.Int, paired.Tag);
        Assert.Equal(42, header.FloatPairedIndex);

        double decoded = Cell.DecodeFloat(header, paired);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(value),
            BitConverter.DoubleToInt64Bits(decoded));
    }

    [Fact]
    public void Float_NegativeZero_PreservesSignBit()
    {
        // -0.0 is excluded from the Theory above because xUnit treats it as a duplicate of
        // 0.0 (they compare equal as doubles). The point of this test is to verify the
        // sign bit survives the round trip, which only a bit-pattern check can confirm.
        var (header, paired) = Cell.MakeFloat(-0.0, pairedHeapIdx: 42);
        double decoded = Cell.DecodeFloat(header, paired);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(-0.0),
            BitConverter.DoubleToInt64Bits(decoded));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Float_HeaderEncodesPairedIndex(int pairedIdx)
    {
        var (header, _) = Cell.MakeFloat(1.5, pairedIdx);
        Assert.Equal(pairedIdx, header.FloatPairedIndex);
    }

    // ---------- Equality ----------

    [Fact]
    public void Equality_IsBitwise()
    {
        var a1 = Cell.Atom(42);
        var a2 = Cell.Atom(42);
        var a3 = Cell.Atom(43);

        Assert.Equal(a1, a2);
        Assert.True(a1 == a2);
        Assert.False(a1 != a2);
        Assert.True(a1.Equals(a2));
        Assert.True(a1.Equals((object)a2));

        Assert.NotEqual(a1, a3);
        Assert.False(a1 == a3);
        Assert.True(a1 != a3);
    }

    [Fact]
    public void Equality_DistinguishesTagFromPayload()
    {
        // Same low 32 bits, different tag → different cells.
        var atom = Cell.Atom(42);
        var refCell = Cell.Ref(42);
        Assert.NotEqual(atom, refCell);
        Assert.True(atom != refCell);
    }

    [Fact]
    public void Equals_WithNonCellObject_ReturnsFalse()
    {
        var c = Cell.Atom(42);
        Assert.False(c.Equals("not a cell"));
        Assert.False(c.Equals(null));
    }

    [Fact]
    public void GetHashCode_ConsistentWithEquals()
    {
        var a = Cell.Atom(42);
        var b = Cell.Atom(42);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ---------- ToString ----------

    [Fact]
    public void ToString_ContainsTagAndPayload()
    {
        var c = Cell.Atom(0x2A);
        string s = c.ToString();
        Assert.Contains("Atom", s);
        Assert.Contains("2A", s);  // payload in hex
    }
}
