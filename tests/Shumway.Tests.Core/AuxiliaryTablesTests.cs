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
        var engine = new Engine();
        var value = BigInteger.Parse("123456789012345678901234567890");
        Cell cell = engine.MakeBigInt(value);
        Assert.Equal(Tag.BigInt, cell.Tag);
        Assert.Equal(value, engine.AsBigInt(cell));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("99999999999999999999999999")]
    [InlineData("-99999999999999999999999999")]
    public void MakeBigInt_AcceptsRange(string decimalLiteral)
    {
        var engine = new Engine();
        var value = BigInteger.Parse(decimalLiteral);
        Cell cell = engine.MakeBigInt(value);
        Assert.Equal(value, engine.AsBigInt(cell));
    }

    [Fact]
    public void MakeBigInt_DistinctCallsGetDistinctIds()
    {
        var engine = new Engine();
        Cell a = engine.MakeBigInt(new BigInteger(1));
        Cell b = engine.MakeBigInt(new BigInteger(1));   // same value, separate slot
        Assert.NotEqual(a.AsBigIntId, b.AsBigIntId);
        Assert.Equal(2, engine.BigIntTableCount);
    }

    [Fact]
    public void AsBigInt_OnWrongTag_Throws()
    {
        var engine = new Engine();
        Assert.Throws<InvalidOperationException>(() => engine.AsBigInt(Cell.Atom(0)));
    }

    // ---------- String ----------

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("emoji 😀 still works")]   // surrogate pair
    public void MakeString_RoundTrips(string value)
    {
        var engine = new Engine();
        Cell cell = engine.MakeString(value);
        Assert.Equal(Tag.String, cell.Tag);
        Assert.Equal(value, engine.AsString(cell));
    }

    [Fact]
    public void MakeString_DoesNotDeduplicate()
    {
        // The string table is append-only; identical content goes into separate slots.
        // Deduplication is the atom table's job; STRING cells are opaque.
        var engine = new Engine();
        Cell a = engine.MakeString("foo");
        Cell b = engine.MakeString("foo");
        Assert.NotEqual(a.AsStringId, b.AsStringId);
    }

    [Fact]
    public void MakeString_Null_Throws()
    {
        var engine = new Engine();
        Assert.Throws<ArgumentNullException>(() => engine.MakeString(null!));
    }

    [Fact]
    public void AsString_OnWrongTag_Throws()
    {
        var engine = new Engine();
        Assert.Throws<InvalidOperationException>(() => engine.AsString(Cell.Atom(0)));
    }

    // ---------- Foreign ----------

    [Fact]
    public void MakeForeign_RoundTripsObject()
    {
        var engine = new Engine();
        var obj = new object();
        Cell cell = engine.MakeForeign(obj);
        Assert.Equal(Tag.Foreign, cell.Tag);
        Assert.Same(obj, engine.AsForeign(cell));
    }

    [Fact]
    public void MakeForeign_AcceptsNull()
    {
        var engine = new Engine();
        Cell cell = engine.MakeForeign(null);
        Assert.Equal(Tag.Foreign, cell.Tag);
        Assert.Null(engine.AsForeign(cell));
    }

    [Fact]
    public void AsForeignTyped_CastsToType()
    {
        var engine = new Engine();
        var list = new List<int> { 1, 2, 3 };
        Cell cell = engine.MakeForeign(list);
        var roundTripped = engine.AsForeign<List<int>>(cell);
        Assert.Same(list, roundTripped);
    }

    [Fact]
    public void AsForeignTyped_OnNullValue_ReturnsNull()
    {
        var engine = new Engine();
        Cell cell = engine.MakeForeign(null);
        Assert.Null(engine.AsForeign<List<int>>(cell));
    }

    [Fact]
    public void AsForeignTyped_WrongType_Throws()
    {
        var engine = new Engine();
        Cell cell = engine.MakeForeign("a string");
        Assert.Throws<InvalidCastException>(() => engine.AsForeign<List<int>>(cell));
    }

    [Fact]
    public void AsForeign_OnWrongTag_Throws()
    {
        var engine = new Engine();
        Assert.Throws<InvalidOperationException>(() => engine.AsForeign(Cell.Atom(0)));
    }

    // ---------- Unify: BigInt ----------

    [Fact]
    public void Unify_BigIntsEqual_Succeeds()
    {
        var engine = new Engine();
        var big = BigInteger.Parse("999999999999999999999");
        int a = engine.AllocateHeap(1); engine.SetHeap(a, engine.MakeBigInt(big));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeBigInt(big));
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_BigIntsDifferent_Fails()
    {
        var engine = new Engine();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, engine.MakeBigInt(BigInteger.Parse("100000000000000000000")));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeBigInt(BigInteger.Parse("100000000000000000001")));
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_BigIntVsInt_Fails()
    {
        // Tag mismatch: Int and BigInt are distinct representations even if a BigInt's
        // value would fit in 60 bits. The canonical form is for callers to normalise.
        var engine = new Engine();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, Cell.Int(5));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeBigInt(new BigInteger(5)));
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_VarWithBigInt_CopiesCellIntoVar()
    {
        var engine = new Engine();
        int v = engine.AllocateHeapUnbound();
        int b = engine.AllocateHeap(1);
        Cell bigCell = engine.MakeBigInt(BigInteger.Parse("999999999999999999999"));
        engine.SetHeap(b, bigCell);

        Assert.True(engine.Unify(v, b));
        Assert.Equal(bigCell, engine.GetHeap(v));
        Assert.Equal(BigInteger.Parse("999999999999999999999"), engine.AsBigInt(engine.GetHeap(v)));
    }

    // ---------- Unify: String ----------

    [Fact]
    public void Unify_StringsEqualContent_SucceedsEvenWhenIdsDiffer()
    {
        var engine = new Engine();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, engine.MakeString("hello"));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeString("hello"));   // distinct id
        Assert.NotEqual(engine.GetHeap(a).AsStringId, engine.GetHeap(b).AsStringId);
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_StringsDifferentContent_Fails()
    {
        var engine = new Engine();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, engine.MakeString("hello"));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeString("world"));
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_StringVsAtom_Fails()
    {
        var engine = new Engine();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, engine.MakeString("hello"));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, Cell.Atom(0));
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_VarWithString_CopiesCellIntoVar()
    {
        var engine = new Engine();
        int v = engine.AllocateHeapUnbound();
        int s = engine.AllocateHeap(1);
        Cell strCell = engine.MakeString("hello");
        engine.SetHeap(s, strCell);

        Assert.True(engine.Unify(v, s));
        Assert.Equal(strCell, engine.GetHeap(v));
    }

    // ---------- Unify: Foreign ----------

    [Fact]
    public void Unify_ForeignsSameReference_Succeeds()
    {
        var engine = new Engine();
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
        var engine = new Engine();
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
        var engine = new Engine();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, engine.MakeForeign(null));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, engine.MakeForeign(null));
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_ForeignNullVsNonNull_Fails()
    {
        var engine = new Engine();
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
        var e1 = new Engine();
        var e2 = new Engine();
        e2.MakeForeign(new object());   // grow e2 so the stranger id is in range for e1

        var foreign1 = e1.MakeForeign("shared");
        // Looking up `foreign1`'s id (0) against e2's table fetches a different object
        // (or null) — we just verify we can do the call without throwing.
        _ = e2.AsForeign(foreign1);
    }

    [Fact]
    public void Unify_VarWithForeign_CopiesCellIntoVar()
    {
        var engine = new Engine();
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
