using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class EnvFrameTests
{
    // ---------- Layout constants ----------

    [Fact]
    public void FrameLayoutConstants_MatchAdr005()
    {
        Assert.Equal(0, Activation.EnvCeOffset);
        Assert.Equal(1, Activation.EnvCpOffset);
        Assert.Equal(2, Activation.EnvNOffset);        // ADR-016: live-perm count
        Assert.Equal(3, Activation.EnvY1Offset);
        Assert.Equal(3, Activation.EnvSize(0));
        Assert.Equal(3 + 5, Activation.EnvSize(5));
    }

    // ---------- Allocate ----------

    [Fact]
    public void Allocate_FirstFrame_SavesPriorRegisters()
    {
        var engine = new Activation();
        engine.SetCp(100);
        Assert.Equal(-1, engine.E);
        Assert.Equal(100, engine.Cp);

        engine.Allocate(2);

        Assert.Equal(0, engine.E);                 // newE = previous stackTop = 0
        Assert.Equal(5, engine.StackTop);          // 3 control (CE,CP,N) + 2 Y slots
        // Control slots are RawInt-tagged (ADR-016); read with (int)Data.
        Assert.Equal(-1, (int)engine.GetStack(0).Data); // CE = previous _e
        Assert.Equal(100, (int)engine.GetStack(1).Data); // CP = previous _cp
        Assert.Equal(2, (int)engine.GetStack(2).Data);  // N = permanent count (ADR-016)

        // Y slots are left UNINITIALISED (lazy allocation): RawInt(0), a
        // GC-skipped sentinel overwritten at the permanent's first occurrence.
        AssertUninitialisedYSlot(engine.GetStack(3));
        AssertUninitialisedYSlot(engine.GetStack(4));
    }

    /// <summary>Asserts that the cell is the uninitialised-Y-slot sentinel that
    /// <c>allocate</c> leaves: <see cref="Tag.RawInt"/> (the heap GC skips it; no
    /// per-permanent heap cell is allocated).</summary>
    private static void AssertUninitialisedYSlot(Cell c)
    {
        Assert.Equal(Tag.RawInt, c.Tag);
        Assert.Equal(0, (int)c.Data);
    }

    [Fact]
    public void Allocate_DoesNotGrowTheHeap()
    {
        // Lazy Y-slot allocation: `allocate` must NOT allocate a heap cell per
        // permanent (the old behaviour generated one dead heap cell per Y slot,
        // driving the heap GC in permanent-heavy loops). The Y slots are the
        // uninitialised RawInt sentinel until first written.
        var engine = new Activation();
        int heapBefore = engine.HeapTop;
        engine.Allocate(8);
        Assert.Equal(heapBefore, engine.HeapTop);   // zero heap growth
    }

    [Fact]
    public void Allocate_ZeroPermanents_OnlyControlSlots()
    {
        var engine = new Activation();
        engine.Allocate(0);
        Assert.Equal(0, engine.E);
        Assert.Equal(3, engine.StackTop);          // CE, CP, N
    }

    [Fact]
    public void Allocate_NestedFrames_CeChains()
    {
        var engine = new Activation();
        engine.SetCp(7);
        engine.Allocate(1);                         // frame at idx 0, size 4 (CE,CP,N,Y1)
        int firstFrame = 0;
        Assert.Equal(firstFrame, engine.E);
        Assert.Equal(4, engine.StackTop);

        engine.SetCp(8);
        engine.Allocate(2);                         // frame at idx 4, size 5
        int secondFrame = 4;
        Assert.Equal(secondFrame, engine.E);
        Assert.Equal(9, engine.StackTop);

        // Second frame's CE points at the first frame; CP is the most-recent _cp.
        Assert.Equal(firstFrame, (int)engine.GetStack(secondFrame + Activation.EnvCeOffset).Data);
        Assert.Equal(8, (int)engine.GetStack(secondFrame + Activation.EnvCpOffset).Data);

        // First frame's contents are unchanged by the second Allocate.
        Assert.Equal(-1, (int)engine.GetStack(firstFrame + Activation.EnvCeOffset).Data);
        Assert.Equal(7, (int)engine.GetStack(firstFrame + Activation.EnvCpOffset).Data);
    }

    [Fact]
    public void Allocate_NegativeCount_Throws()
    {
        var engine = new Activation();
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Allocate(-1));
    }

    [Fact]
    public void Allocate_GrowsStackOnOverflow()
    {
        var engine = new Activation(new ActivationConfig { InitialStackSize = 3 });
        Assert.Equal(3, engine.StackCapacity);

        engine.Allocate(0);                         // fills the initial budget (size 3)
        Assert.Equal(3, engine.StackCapacity);

        engine.Allocate(0);                         // forces growth
        Assert.True(engine.StackCapacity >= 6);
        Assert.Equal(6, engine.StackTop);
    }

    [Fact]
    public void Allocate_PreservesPriorFramesAcrossStackGrowth()
    {
        var engine = new Activation(new ActivationConfig { InitialStackSize = 2 });
        engine.SetCp(99);
        engine.Allocate(0);                         // frame at idx 0

        engine.SetCp(100);
        engine.Allocate(2);                         // forces growth

        // First frame's saved CP should still be readable.
        Assert.Equal(99, (int)engine.GetStack(0 + Activation.EnvCpOffset).Data);
    }

    [Fact]
    public void Allocate_MaxStackSizeExceeded_Throws()
    {
        var engine = new Activation(new ActivationConfig { InitialStackSize = 3, MaxStackSize = 3 });
        engine.Allocate(0);                         // exactly fills the cap (size 3)
        Assert.Throws<InvalidOperationException>(() => engine.Allocate(0));
    }

    // ---------- Deallocate ----------

    [Fact]
    public void Deallocate_RestoresPriorRegisters()
    {
        var engine = new Activation();
        engine.SetCp(42);
        engine.Allocate(2);

        engine.Deallocate();

        Assert.Equal(-1, engine.E);
        Assert.Equal(42, engine.Cp);
        // No choice point protects the frame (_b = -1 < the frame at 0), so
        // deallocate reclaims its space — _stackTop drops back to the frame's
        // start (standard WAM environment trimming on deallocate).
        Assert.Equal(0, engine.StackTop);
    }

    [Fact]
    public void Deallocate_KeepsFrame_WhenChoicePointProtectsIt()
    {
        // A choice point opened during the clause body sits above the frame, so
        // the frame must survive deallocate — a backtrack into the CP could
        // reactivate it. Reclaiming would corrupt the CP's saved slots.
        var engine = new Activation();
        engine.SetCp(42);
        engine.Allocate(2);                 // frame at 0
        engine.PushChoicePoint(0, 100);     // CP above the frame: _b >= 0 frame
        int stackTopBefore = engine.StackTop;

        engine.Deallocate();

        // _b (the CP) is above the frame, so the reclamation is a no-op.
        Assert.Equal(stackTopBefore, engine.StackTop);
    }

    [Fact]
    public void Deallocate_NestedFrames_PopsOneAtATime()
    {
        // Allocate captures the *current* _cp into the new frame, so each Deallocate
        // restores _cp to the value that was current at its corresponding Allocate.
        var engine = new Activation();
        engine.SetCp(7);
        engine.Allocate(0);                         // frame 0 saves CP=7
        engine.SetCp(8);
        engine.Allocate(0);                         // frame 3 saves CP=8 (EnvSize(0)=3)
        engine.SetCp(9);
        engine.Allocate(0);                         // frame 6 saves CP=9

        engine.Deallocate();                        // pops frame 6 → CP=9, E=3
        Assert.Equal(3, engine.E);
        Assert.Equal(9, engine.Cp);

        engine.Deallocate();                        // pops frame 3 → CP=8, E=0
        Assert.Equal(0, engine.E);
        Assert.Equal(8, engine.Cp);

        engine.Deallocate();                        // pops frame 0 → CP=7, E=-1
        Assert.Equal(-1, engine.E);
        Assert.Equal(7, engine.Cp);
    }

    [Fact]
    public void Deallocate_NoFrame_Throws()
    {
        var engine = new Activation();
        Assert.Throws<InvalidOperationException>(() => engine.Deallocate());
    }

    [Fact]
    public void AllocateDeallocate_RoundTripsRegistersInWamConvention()
    {
        // The WAM convention is allocate ... deallocate proceed, where deallocate restores
        // CE/CP and proceed jumps to CP. The pair must be transparent to the caller.
        var engine = new Activation();
        engine.SetCp(999);
        int eBefore = engine.E;
        int cpBefore = engine.Cp;

        engine.Allocate(3);
        engine.Deallocate();

        Assert.Equal(eBefore, engine.E);
        Assert.Equal(cpBefore, engine.Cp);
    }

    // ---------- GetY / SetY ----------

    [Fact]
    public void GetY_WithinFrame_ReadsCorrectSlot()
    {
        var engine = new Activation();
        engine.Allocate(3);
        engine.SetY(1, Cell.Atom(42));
        Assert.Equal(Cell.Atom(42), engine.GetY(1));

        // The other two slots are still the uninitialised sentinel.
        AssertUninitialisedYSlot(engine.GetY(0));
        AssertUninitialisedYSlot(engine.GetY(2));
    }

    [Fact]
    public void GetY_NoActiveFrame_Throws()
    {
        var engine = new Activation();
        Assert.Throws<InvalidOperationException>(() => engine.GetY(0));
    }

    [Fact]
    public void SetY_NoActiveFrame_Throws()
    {
        var engine = new Activation();
        Assert.Throws<InvalidOperationException>(() => engine.SetY(0, Cell.Atom(0)));
    }

    [Fact]
    public void GetY_AfterFrameSwitch_TargetsTheCurrentFrame()
    {
        var engine = new Activation();
        engine.Allocate(1);
        engine.SetY(0, Cell.Atom(10));     // outer Y0 = 10

        engine.Allocate(1);
        engine.SetY(0, Cell.Atom(20));     // inner Y0 = 20
        Assert.Equal(Cell.Atom(20), engine.GetY(0));

        engine.Deallocate();                // back to outer
        Assert.Equal(Cell.Atom(10), engine.GetY(0));
    }
}
