using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class FloatTests
{
    // ---------- MakeFloat layout ----------

    [Fact]
    public void MakeFloat_AllocatesContiguousHeaderAndPaired()
    {
        var engine = new Engine();
        int idx = engine.MakeFloat(1.5);

        Assert.Equal(Tag.Float, engine.GetHeap(idx).Tag);
        Assert.Equal(Tag.Int, engine.GetHeap(idx + 1).Tag);
        Assert.Equal(idx + 1, engine.GetHeap(idx).FloatPairedIndex);
    }

    [Fact]
    public void MakeFloat_AdvancesHeapTopByTwo()
    {
        var engine = new Engine();
        int before = engine.HeapTop;
        engine.MakeFloat(1.0);
        Assert.Equal(before + 2, engine.HeapTop);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(3.141592653589793)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    [InlineData(double.Epsilon)]
    [InlineData(double.NaN)]
    public void MakeFloat_RoundTripsBitExact(double value)
    {
        var engine = new Engine();
        int idx = engine.MakeFloat(value);
        double decoded = Cell.DecodeFloat(engine.GetHeap(idx), engine.GetHeap(idx + 1));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(value),
            BitConverter.DoubleToInt64Bits(decoded));
    }

    [Fact]
    public void MakeFloat_NegativeZero_PreservesSignBit()
    {
        // -0.0 is excluded from the Theory above because xUnit treats it as a duplicate
        // of 0.0 under Equals.
        var engine = new Engine();
        int idx = engine.MakeFloat(-0.0);
        double decoded = Cell.DecodeFloat(engine.GetHeap(idx), engine.GetHeap(idx + 1));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(-0.0),
            BitConverter.DoubleToInt64Bits(decoded));
    }

    // ---------- Unify: Float ↔ Float ----------

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(3.141592653589793)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public void Unify_FloatsBitEqual_Succeeds(double value)
    {
        var engine = new Engine();
        int a = engine.MakeFloat(value);
        int b = engine.MakeFloat(value);
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_NaNWithNaN_Succeeds()
    {
        // SWI-Prolog and the design doc both say NaN unifies with NaN under =/2.
        // Numeric comparison via =:= would fail; that's a different operator.
        var engine = new Engine();
        int a = engine.MakeFloat(double.NaN);
        int b = engine.MakeFloat(double.NaN);
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_PositiveZeroVsNegativeZero_Fails()
    {
        // == returns true for these, but their bit patterns differ. Unify uses bit
        // comparison, so they don't unify — matching the "structural equality" intent.
        var engine = new Engine();
        int a = engine.MakeFloat(0.0);
        int b = engine.MakeFloat(-0.0);
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_DifferentFloats_Fails()
    {
        var engine = new Engine();
        int a = engine.MakeFloat(1.5);
        int b = engine.MakeFloat(2.5);
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_FloatsWithDistinctPairedCells_StillCompareByValue()
    {
        // The two Floats live in separate heap regions but encode the same double.
        var engine = new Engine();
        int a = engine.MakeFloat(7.25);
        engine.AllocateHeap(5);                    // padding between the two
        int b = engine.MakeFloat(7.25);
        Assert.NotEqual(
            engine.GetHeap(a).FloatPairedIndex,
            engine.GetHeap(b).FloatPairedIndex);
        Assert.True(engine.Unify(a, b));
    }

    // ---------- Unify: Float ↔ other tags ----------

    [Fact]
    public void Unify_FloatVsInt_Fails()
    {
        var engine = new Engine();
        int a = engine.MakeFloat(1.0);
        int b = engine.AllocateHeap(1);
        engine.SetHeap(b, Cell.Int(1));
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_FloatVsAtom_Fails()
    {
        var engine = new Engine();
        int a = engine.MakeFloat(0.0);
        int b = engine.AllocateHeap(1);
        engine.SetHeap(b, Cell.Atom(AtomTable.EmptyListId));
        Assert.False(engine.Unify(a, b));
    }

    // ---------- Var ↔ Float ----------

    [Fact]
    public void Unify_VarWithFloat_CopiesHeaderAndPreservesPaired()
    {
        var engine = new Engine();
        int v = engine.AllocateHeapUnbound();
        int f = engine.MakeFloat(3.14);

        Assert.True(engine.Unify(v, f));

        Cell vCell = engine.GetHeap(v);
        Assert.Equal(Tag.Float, vCell.Tag);
        // Copy of the header keeps the original paired-index, which still points at the
        // original paired cell — both Floats are now bit-identical.
        Assert.Equal(engine.GetHeap(f).FloatPairedIndex, vCell.FloatPairedIndex);

        double decoded = Cell.DecodeFloat(vCell, engine.GetHeap(vCell.FloatPairedIndex));
        Assert.Equal(3.14, decoded);
    }

    [Fact]
    public void Unify_FloatWithVar_BindsVarSameWay()
    {
        var engine = new Engine();
        int f = engine.MakeFloat(-2.71);
        int v = engine.AllocateHeapUnbound();

        Assert.True(engine.Unify(f, v));    // bound argument first
        Assert.Equal(Tag.Float, engine.GetHeap(v).Tag);
    }

    [Fact]
    public void Unify_VarBoundToFloat_UnifiesWithEqualFloat()
    {
        // After binding a var to a Float, the var participates in subsequent Float
        // unifications as if it were the original Float.
        var engine = new Engine();
        int v = engine.AllocateHeapUnbound();
        int f1 = engine.MakeFloat(42.0);
        Assert.True(engine.Unify(v, f1));

        int f2 = engine.MakeFloat(42.0);
        Assert.True(engine.Unify(v, f2));   // v still acts as the Float

        int f3 = engine.MakeFloat(43.0);
        Assert.False(engine.Unify(v, f3));
    }
}
