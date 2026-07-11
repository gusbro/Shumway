using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class TrailUnwindTests
{
    // ---------- UnwindTrails: binding-only ----------

    [Fact]
    public void UnwindTrails_BindingOnly_RestoresAll()
    {
        var engine = new Activation();
        int a = engine.AllocateHeapUnbound();
        int b = engine.AllocateHeapUnbound();
        engine.SetHbForTesting(engine.HeapTop);

        engine.Bind(a, Cell.Atom(1));
        engine.Bind(b, Cell.Atom(2));

        engine.UnwindTrails(bindingTarget: 0, extraTarget: 0);
        Assert.Equal(0, engine.BindingTrailTop);
        Assert.Equal(Cell.UnboundVar(a), engine.GetHeap(a));
        Assert.Equal(Cell.UnboundVar(b), engine.GetHeap(b));
    }

    [Fact]
    public void UnwindTrails_BindingOnly_PartialUnwind()
    {
        var engine = new Activation();
        int a = engine.AllocateHeapUnbound();
        int b = engine.AllocateHeapUnbound();
        int c = engine.AllocateHeapUnbound();
        engine.SetHbForTesting(engine.HeapTop);

        engine.Bind(a, Cell.Atom(1));
        engine.Bind(b, Cell.Atom(2));
        engine.Bind(c, Cell.Atom(3));

        engine.UnwindTrails(bindingTarget: 1, extraTarget: 0);
        Assert.Equal(1, engine.BindingTrailTop);
        Assert.Equal(Cell.Atom(1), engine.GetHeap(a));            // first binding kept
        Assert.Equal(Cell.UnboundVar(b), engine.GetHeap(b));      // rolled back
        Assert.Equal(Cell.UnboundVar(c), engine.GetHeap(c));      // rolled back
    }

    // ---------- TrailValueChange + extra trail ----------

    [Fact]
    public void TrailValueChange_AppendsExtraTrailEntry()
    {
        var engine = new Activation();
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, Cell.Atom(7));
        engine.TrailValueChange(slot, Cell.Atom(7));
        Assert.Equal(1, engine.ExtraTrailTop);
    }

    [Fact]
    public void UnwindTrails_ExtraOnly_RestoresOldValue()
    {
        var engine = new Activation();
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, Cell.Atom(7));
        engine.TrailValueChange(slot, Cell.Atom(7));
        engine.SetHeap(slot, Cell.Atom(8));

        engine.UnwindTrails(0, 0);
        Assert.Equal(Cell.Atom(7), engine.GetHeap(slot));
        Assert.Equal(0, engine.ExtraTrailTop);
    }

    [Fact]
    public void ExtraTrail_GrowsWhenInitialCapacityExceeded()
    {
        var engine = new Activation(new ActivationConfig { InitialExtraTrailSize = 2 });
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, Cell.Atom(0));
        engine.TrailValueChange(slot, Cell.Atom(0));
        engine.TrailValueChange(slot, Cell.Atom(0));
        engine.TrailValueChange(slot, Cell.Atom(0));
        Assert.True(engine.ExtraTrailCapacity >= 3);
        Assert.Equal(3, engine.ExtraTrailTop);
    }

    // ---------- Interleaved unwind (the ADR-004 algorithm) ----------

    [Fact]
    public void UnwindTrails_InterleavedBindingsAndExtras_UndoneInReverseOrder()
    {
        // Sequence: bind X, change cell C, bind Y. Trail state captures the marker so
        // unwind first rolls Y back, then restores C, then rolls X back.
        var engine = new Activation();
        int x = engine.AllocateHeapUnbound();
        int y = engine.AllocateHeapUnbound();
        int cSlot = engine.AllocateHeap(1);
        engine.SetHeap(cSlot, Cell.Atom(10));
        engine.SetHbForTesting(engine.HeapTop);

        engine.Bind(x, Cell.Atom(1));                       // bindingTrail: [x]
        engine.TrailValueChange(cSlot, Cell.Atom(10));      // extra marker = 1
        engine.SetHeap(cSlot, Cell.Atom(20));
        engine.Bind(y, Cell.Atom(2));                       // bindingTrail: [x, y]

        engine.UnwindTrails(0, 0);

        Assert.Equal(Cell.UnboundVar(x), engine.GetHeap(x));
        Assert.Equal(Cell.UnboundVar(y), engine.GetHeap(y));
        Assert.Equal(Cell.Atom(10), engine.GetHeap(cSlot));
        Assert.Equal(0, engine.BindingTrailTop);
        Assert.Equal(0, engine.ExtraTrailTop);
    }

    [Fact]
    public void UnwindTrails_PartialKeepsEarlierMutations()
    {
        // Sequence: bind X (keep), value-change C (keep), bind Y (rollback). Unwind to
        // targets that preserve the first binding and the value change.
        var engine = new Activation();
        int x = engine.AllocateHeapUnbound();
        int y = engine.AllocateHeapUnbound();
        int cSlot = engine.AllocateHeap(1);
        engine.SetHeap(cSlot, Cell.Atom(10));
        engine.SetHbForTesting(engine.HeapTop);

        engine.Bind(x, Cell.Atom(1));
        int bindingMark = engine.BindingTrailTop;       // 1
        engine.TrailValueChange(cSlot, Cell.Atom(10));
        engine.SetHeap(cSlot, Cell.Atom(20));
        int extraMark = engine.ExtraTrailTop;           // 1
        engine.Bind(y, Cell.Atom(2));

        engine.UnwindTrails(bindingTarget: bindingMark, extraTarget: extraMark);
        Assert.Equal(Cell.Atom(1), engine.GetHeap(x));   // preserved
        Assert.Equal(Cell.Atom(20), engine.GetHeap(cSlot)); // preserved
        Assert.Equal(Cell.UnboundVar(y), engine.GetHeap(y)); // rolled back
    }

    [Fact]
    public void UnwindTrails_MultipleExtrasOnly()
    {
        var engine = new Activation();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, Cell.Atom(1));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, Cell.Atom(2));

        engine.TrailValueChange(a, Cell.Atom(1));
        engine.SetHeap(a, Cell.Atom(11));
        engine.TrailValueChange(b, Cell.Atom(2));
        engine.SetHeap(b, Cell.Atom(22));

        engine.UnwindTrails(0, 0);
        Assert.Equal(Cell.Atom(1), engine.GetHeap(a));
        Assert.Equal(Cell.Atom(2), engine.GetHeap(b));
    }

    // ---------- Validation ----------

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(1000, 0)]
    [InlineData(0, 1000)]
    public void UnwindTrails_InvalidTargets_Throw(int bindingTarget, int extraTarget)
    {
        var engine = new Activation();
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.UnwindTrails(bindingTarget, extraTarget));
    }
}
