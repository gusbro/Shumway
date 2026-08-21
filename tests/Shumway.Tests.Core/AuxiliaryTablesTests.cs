using System.Numerics;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class AuxiliaryTablesTests
{
    // ---------- BigInt ----------

    [Fact]
    public void MakeBigInt_RoundTripsValue()
    {
        var engine = new Activation();
        var value = BigInteger.Parse("123456789012345678901234567890");
        Cell cell = engine.MakeBigInt(value);
        Assert.Equal(Tag.BigInt, cell.Tag);
        Assert.Equal(value, engine.AsBigInt(cell));
    }

    [Theory]
    [InlineData("99999999999999999999999999")]
    [InlineData("-99999999999999999999999999")]
    public void MakeBigInt_AcceptsRange(string decimalLiteral)
    {
        // Values must be outside the 60-bit inline range — anything that
        // fits inline auto-collapses to Tag.Int to keep cell representations
        // canonical for unification.
        var engine = new Activation();
        var value = BigInteger.Parse(decimalLiteral);
        Cell cell = engine.MakeBigInt(value);
        Assert.Equal(Tag.BigInt, cell.Tag);
        Assert.Equal(value, engine.AsBigInt(cell));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("576460752303423487")]    // 2^59 - 1 (MaxInt60)
    [InlineData("-576460752303423488")]   // -2^59 (MinInt60)
    public void MakeBigInt_CollapsesToInlineWhenWithinSixtyBitRange(string decimalLiteral)
    {
        // Values inside the 60-bit signed range collapse to Tag.Int so that
        // unification doesn't have to cross tag boundaries to recognise that
        // BigInteger(5) and Int(5) represent the same value (ADR-013).
        var engine = new Activation();
        var value = BigInteger.Parse(decimalLiteral);
        Cell cell = engine.MakeBigInt(value);
        Assert.Equal(Tag.Int, cell.Tag);
        Assert.Equal((long)value, cell.AsInt);
    }

    [Fact]
    public void MakeBigInt_DistinctCallsGetDistinctIds()
    {
        // Two BigInt cells with the same outside-range value get separate
        // side-table slots. The collapse only fires for inline-range values.
        var engine = new Activation();
        var big = BigInteger.Parse("999999999999999999999");
        Cell a = engine.MakeBigInt(big);
        Cell b = engine.MakeBigInt(big);
        Assert.NotEqual(a.AsBigIntId, b.AsBigIntId);
        Assert.Equal(2, engine.BigIntTableCount);
    }

    [Fact]
    public void AsBigInt_OnWrongTag_Throws()
    {
        var engine = new Activation();
        Assert.Throws<InvalidOperationException>(() => engine.AsBigInt(Cell.Atom(0)));
    }


    // ---------- Foreign ----------

    [Fact]
    public void MakeForeign_RoundTripsObject()
    {
        var engine = new Activation();
        var obj = new object();
        Cell cell = engine.MakeForeign(obj);
        Assert.Equal(Tag.Foreign, cell.Tag);
        Assert.Same(obj, engine.AsForeign(cell));
    }

    [Fact]
    public void MakeForeign_AcceptsNull()
    {
        var engine = new Activation();
        Cell cell = engine.MakeForeign(null);
        Assert.Equal(Tag.Foreign, cell.Tag);
        Assert.Null(engine.AsForeign(cell));
    }

    [Fact]
    public void AsForeignTyped_CastsToType()
    {
        var engine = new Activation();
        var list = new List<int> { 1, 2, 3 };
        Cell cell = engine.MakeForeign(list);
        var roundTripped = engine.AsForeign<List<int>>(cell);
        Assert.Same(list, roundTripped);
    }

    [Fact]
    public void AsForeignTyped_OnNullValue_ReturnsNull()
    {
        var engine = new Activation();
        Cell cell = engine.MakeForeign(null);
        Assert.Null(engine.AsForeign<List<int>>(cell));
    }

    [Fact]
    public void AsForeignTyped_WrongType_Throws()
    {
        var engine = new Activation();
        Cell cell = engine.MakeForeign("a string");
        Assert.Throws<InvalidCastException>(() => engine.AsForeign<List<int>>(cell));
    }

    [Fact]
    public void AsForeign_OnWrongTag_Throws()
    {
        var engine = new Activation();
        Assert.Throws<InvalidOperationException>(() => engine.AsForeign(Cell.Atom(0)));
    }

    // ---------- Unify: BigInt ----------

    [Fact]
    public void Unify_BigIntsEqual_Succeeds()
    {
        var engine = new Activation();
        var big = BigInteger.Parse("999999999999999999999");
        int a = engine.AllocateHeap(1); engine.SetHeap(a, engine.MakeBigInt(big));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeBigInt(big));
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_BigIntsDifferent_Fails()
    {
        var engine = new Activation();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, engine.MakeBigInt(BigInteger.Parse("100000000000000000000")));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeBigInt(BigInteger.Parse("100000000000000000001")));
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_BigIntInRange_CollapsesAndMatchesInlineInt()
    {
        // MakeBigInt auto-collapses 60-bit-fitting values to Tag.Int — so an
        // explicit Int(5) and MakeBigInt(5) end up structurally identical and
        // unify. The invariant (ADR-013) is that the canonical form for a
        // 60-bit-fitting integer is always Tag.Int, regardless of how it was
        // produced.
        var engine = new Activation();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, Cell.Int(5));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeBigInt(new BigInteger(5)));
        Assert.Equal(Tag.Int, engine.GetHeap(b).Tag);
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_VarWithBigInt_CopiesCellIntoVar()
    {
        var engine = new Activation();
        int v = engine.AllocateHeapUnbound();
        int b = engine.AllocateHeap(1);
        Cell bigCell = engine.MakeBigInt(BigInteger.Parse("999999999999999999999"));
        engine.SetHeap(b, bigCell);

        Assert.True(engine.Unify(v, b));
        Assert.Equal(bigCell, engine.GetHeap(v));
        Assert.Equal(BigInteger.Parse("999999999999999999999"), engine.AsBigInt(engine.GetHeap(v)));
    }


    // ---------- Unify: Foreign ----------

    [Fact]
    public void Unify_ForeignsSameReference_Succeeds()
    {
        var engine = new Activation();
        var shared = new object();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, engine.MakeForeign(shared));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeForeign(shared));
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_ForeignsDifferentReferences_Fails_EvenWhenEqualByEquals()
    {
        // Reference semantics: two separate instances with .Equals true (e.g. string
        // boxed as object) still fail Unify, per cell-layout-detail.md.
        var engine = new Activation();
        var s1 = new string('x', 5);
        var s2 = new string('x', 5);
        Assert.False(ReferenceEquals(s1, s2));
        Assert.Equal(s1, s2);

        int a = engine.AllocateHeap(1); engine.SetHeap(a, engine.MakeForeign(s1));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeForeign(s2));
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_ForeignsBothNull_Succeeds()
    {
        // ReferenceEquals(null, null) is true.
        var engine = new Activation();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, engine.MakeForeign(null));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeForeign(null));
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_ForeignNullVsNonNull_Fails()
    {
        var engine = new Activation();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, engine.MakeForeign(null));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeForeign(new object()));
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_ForeignVsForeignAcrossEnginesIsUndefined()
    {
        // Cells from a different engine reference that engine's foreign table. Looking
        // them up against this engine's table would either index out of range or pull
        // an unrelated object — the API does not promise meaningful behaviour, but it
        // must not throw silently for an in-range stranger id, so we just document the
        // shape here without asserting a specific outcome.
        var e1 = new Activation();
        var e2 = new Activation();
        e2.MakeForeign(new object());   // grow e2 so the stranger id is in range for e1

        var foreign1 = e1.MakeForeign("shared");
        // Looking up `foreign1`'s id (0) against e2's table fetches a different object
        // (or null) — we just verify we can do the call without throwing.
        _ = e2.AsForeign(foreign1);
    }

    [Fact]
    public void Unify_VarWithForeign_CopiesCellIntoVar()
    {
        var engine = new Activation();
        int v = engine.AllocateHeapUnbound();
        int f = engine.AllocateHeap(1);
        var obj = new object();
        Cell foreignCell = engine.MakeForeign(obj);
        engine.SetHeap(f, foreignCell);

        Assert.True(engine.Unify(v, f));
        Assert.Equal(foreignCell, engine.GetHeap(v));
        Assert.Same(obj, engine.AsForeign(engine.GetHeap(v)));
    }
}
