using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class DerefBindTests
{
    // ---------- Deref ----------

    [Fact]
    public void Deref_UnboundVar_ReturnsSelf()
    {
        var engine = new Activation();
        int v = engine.AllocateHeapUnbound();
        Assert.Equal(v, engine.Deref(v));
    }

    [Fact]
    public void Deref_AtomicCell_ReturnsSelf()
    {
        var engine = new Activation();
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, Cell.Atom(42));
        Assert.Equal(slot, engine.Deref(slot));
    }

    [Fact]
    public void Deref_RefPointingElsewhere_FollowsOnce()
    {
        var engine = new Activation();
        int target = engine.AllocateHeap(1);
        engine.SetHeap(target, Cell.Atom(7));
        int via = engine.AllocateHeap(1);
        engine.SetHeap(via, Cell.Ref(target));
        Assert.Equal(target, engine.Deref(via));
    }

    [Fact]
    public void Deref_FollowsChainToFinalCell()
    {
        var engine = new Activation();
        int target = engine.AllocateHeap(1);
        engine.SetHeap(target, Cell.Atom(99));
        int mid = engine.AllocateHeap(1);
        engine.SetHeap(mid, Cell.Ref(target));
        int start = engine.AllocateHeap(1);
        engine.SetHeap(start, Cell.Ref(mid));

        Assert.Equal(target, engine.Deref(start));
    }

    [Fact]
    public void Deref_RefToSelf_TreatedAsUnbound()
    {
        var engine = new Activation();
        int v = engine.AllocateHeap(1);
        engine.SetHeap(v, Cell.Ref(v));   // self-pointing REF
        Assert.Equal(v, engine.Deref(v));
    }

    // ---------- Bind: trailing decision based on HB ----------

    [Fact]
    public void Bind_NoChoicePoint_NoTrailEntry()
    {
        var engine = new Activation();
        int v = engine.AllocateHeapUnbound();
        // Hb is 0 by default — v is "young", binding is not trailed.
        engine.Bind(v, Cell.Atom(5));
        Assert.Equal(0, engine.BindingTrailTop);
        Assert.Equal(Cell.Atom(5), engine.GetHeap(v));
    }

    [Fact]
    public void Bind_VarOlderThanHb_IsTrailed()
    {
        var engine = new Activation();
        int v = engine.AllocateHeapUnbound();         // idx 0
        engine.AllocateHeap(1);                       // idx 1, advance heap top
        engine.SetHbForTesting(1);                    // HB = 1, so var 0 is "old" (0 < 1)

        engine.Bind(v, Cell.Atom(10));
        Assert.Equal(1, engine.BindingTrailTop);
        Assert.Equal(v, engine.BindingTrailSpan[0]);
    }

    [Fact]
    public void Bind_VarEqualToHb_IsNotTrailed()
    {
        // HB check is strictly less-than: a var at exactly heap[Hb] is "young".
        var engine = new Activation();
        engine.AllocateHeap(2);
        int v = engine.AllocateHeapUnbound();         // idx 2
        engine.SetHbForTesting(v);                    // HB = 2

        engine.Bind(v, Cell.Atom(3));
        Assert.Equal(0, engine.BindingTrailTop);
    }

    [Fact]
    public void Bind_OverwritesCell()
    {
        var engine = new Activation();
        int v = engine.AllocateHeapUnbound();
        engine.Bind(v, Cell.Int(42));
        Assert.Equal(Cell.Int(42), engine.GetHeap(v));
    }

    [Fact]
    public void Bind_MultipleOldVars_AppendsTrailInOrder()
    {
        var engine = new Activation();
        int a = engine.AllocateHeapUnbound();
        int b = engine.AllocateHeapUnbound();
        int c = engine.AllocateHeapUnbound();
        engine.SetHbForTesting(engine.HeapTop);       // all three are "old"

        engine.Bind(a, Cell.Atom(1));
        engine.Bind(b, Cell.Atom(2));
        engine.Bind(c, Cell.Atom(3));

        Assert.Equal(3, engine.BindingTrailTop);
        Assert.Equal(a, engine.BindingTrailSpan[0]);
        Assert.Equal(b, engine.BindingTrailSpan[1]);
        Assert.Equal(c, engine.BindingTrailSpan[2]);
    }

    [Fact]
    public void BindingTrail_GrowsWhenInitialCapacityExceeded()
    {
        var engine = new Activation(new ActivationConfig { InitialBindingTrailSize = 2 });
        int a = engine.AllocateHeapUnbound();
        int b = engine.AllocateHeapUnbound();
        int c = engine.AllocateHeapUnbound();
        engine.SetHbForTesting(engine.HeapTop);

        engine.Bind(a, Cell.Atom(0));
        engine.Bind(b, Cell.Atom(0));
        engine.Bind(c, Cell.Atom(0));                 // forces trail growth

        Assert.True(engine.BindingTrailCapacity >= 3);
        Assert.Equal(3, engine.BindingTrailTop);
    }

    // ---------- UnwindBindingTrail ----------

    [Fact]
    public void UnwindBindingTrail_RestoresCellsToUnbound()
    {
        var engine = new Activation();
        int a = engine.AllocateHeapUnbound();
        int b = engine.AllocateHeapUnbound();
        engine.SetHbForTesting(engine.HeapTop);

        engine.Bind(a, Cell.Atom(1));
        engine.Bind(b, Cell.Atom(2));
        Assert.Equal(2, engine.BindingTrailTop);

        engine.UnwindBindingTrail(0);

        Assert.Equal(0, engine.BindingTrailTop);
        Assert.Equal(Cell.UnboundVar(a), engine.GetHeap(a));
        Assert.Equal(Cell.UnboundVar(b), engine.GetHeap(b));
    }

    [Fact]
    public void UnwindBindingTrail_PartialUnwind_LeavesEarlierBindings()
    {
        var engine = new Activation();
        int a = engine.AllocateHeapUnbound();
        int b = engine.AllocateHeapUnbound();
        int c = engine.AllocateHeapUnbound();
        engine.SetHbForTesting(engine.HeapTop);

        engine.Bind(a, Cell.Atom(1));
        engine.Bind(b, Cell.Atom(2));
        engine.Bind(c, Cell.Atom(3));

        engine.UnwindBindingTrail(targetTop: 1);

        Assert.Equal(1, engine.BindingTrailTop);
        Assert.Equal(Cell.Atom(1), engine.GetHeap(a));         // still bound
        Assert.Equal(Cell.UnboundVar(b), engine.GetHeap(b));   // restored
        Assert.Equal(Cell.UnboundVar(c), engine.GetHeap(c));   // restored
    }

    [Fact]
    public void UnwindBindingTrail_NoOp_WhenTargetMatchesTop()
    {
        var engine = new Activation();
        int v = engine.AllocateHeapUnbound();
        engine.SetHbForTesting(engine.HeapTop);
        engine.Bind(v, Cell.Atom(1));

        engine.UnwindBindingTrail(engine.BindingTrailTop);
        Assert.Equal(1, engine.BindingTrailTop);
        Assert.Equal(Cell.Atom(1), engine.GetHeap(v));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void UnwindBindingTrail_OutOfRange_Throws(int badTarget)
    {
        var engine = new Activation();
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.UnwindBindingTrail(badTarget));
    }
}
