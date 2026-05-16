using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class EnvFrameTests
{
    // ---------- Layout constants ----------

    [Fact]
    public void FrameLayoutConstants_MatchAdr005()
    {
        Assert.Equal(0, Engine.EnvCeOffset);
        Assert.Equal(1, Engine.EnvCpOffset);
        Assert.Equal(2, Engine.EnvY1Offset);
        Assert.Equal(2, Engine.EnvSize(0));
        Assert.Equal(2 + 5, Engine.EnvSize(5));
    }

    // ---------- Allocate ----------

    [Fact]
    public void Allocate_FirstFrame_SavesPriorRegisters()
    {
        var engine = new Engine();
        engine.SetCp(100);
        Assert.Equal(-1, engine.E);
        Assert.Equal(100, engine.Cp);

        engine.Allocate(2);

        Assert.Equal(0, engine.E);                 // newE = previous stackTop = 0
        Assert.Equal(4, engine.StackTop);          // 2 + 2 Y slots
        Assert.Equal(-1L, engine.GetStack(0).Data); // CE = previous _e
        Assert.Equal(100L, engine.GetStack(1).Data); // CP = previous _cp
        Assert.Equal(Cell.UnboundVar(2), engine.GetStack(2));
        Assert.Equal(Cell.UnboundVar(3), engine.GetStack(3));
    }

    [Fact]
    public void Allocate_ZeroPermanents_OnlyControlSlots()
    {
        var engine = new Engine();
        engine.Allocate(0);
        Assert.Equal(0, engine.E);
        Assert.Equal(2, engine.StackTop);
    }

    [Fact]
    public void Allocate_NestedFrames_CeChains()
    {
        var engine = new Engine();
        engine.SetCp(7);
        engine.Allocate(1);                         // frame at idx 0, size 3
        int firstFrame = 0;
        Assert.Equal(firstFrame, engine.E);
        Assert.Equal(3, engine.StackTop);

        engine.SetCp(8);
        engine.Allocate(2);                         // frame at idx 3, size 4
        int secondFrame = 3;
        Assert.Equal(secondFrame, engine.E);
        Assert.Equal(7, engine.StackTop);

        // Second frame's CE points at the first frame; CP is the most-recent _cp.
        Assert.Equal((long)firstFrame, engine.GetStack(secondFrame + Engine.EnvCeOffset).Data);
        Assert.Equal(8L, engine.GetStack(secondFrame + Engine.EnvCpOffset).Data);

        // First frame's contents are unchanged by the second Allocate.
        Assert.Equal(-1L, engine.GetStack(firstFrame + Engine.EnvCeOffset).Data);
        Assert.Equal(7L, engine.GetStack(firstFrame + Engine.EnvCpOffset).Data);
    }

    [Fact]
    public void Allocate_NegativeCount_Throws()
    {
        var engine = new Engine();
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Allocate(-1));
    }

    [Fact]
    public void Allocate_GrowsStackOnOverflow()
    {
        var engine = new Engine(new EngineConfig { InitialStackSize = 2 });
        Assert.Equal(2, engine.StackCapacity);

        engine.Allocate(0);                         // fills the initial budget
        Assert.Equal(2, engine.StackCapacity);

        engine.Allocate(0);                         // forces growth
        Assert.True(engine.StackCapacity >= 4);
        Assert.Equal(4, engine.StackTop);
    }

    [Fact]
    public void Allocate_PreservesPriorFramesAcrossStackGrowth()
    {
        var engine = new Engine(new EngineConfig { InitialStackSize = 2 });
        engine.SetCp(99);
        engine.Allocate(0);                         // frame at idx 0

        engine.SetCp(100);
        engine.Allocate(2);                         // forces growth

        // First frame's saved CP should still be readable.
        Assert.Equal(99L, engine.GetStack(0 + Engine.EnvCpOffset).Data);
    }

    [Fact]
    public void Allocate_MaxStackSizeExceeded_Throws()
    {
        var engine = new Engine(new EngineConfig { InitialStackSize = 2, MaxStackSize = 2 });
        engine.Allocate(0);                         // exactly fills the cap
        Assert.Throws<InvalidOperationException>(() => engine.Allocate(0));
    }

    // ---------- Deallocate ----------

    [Fact]
    public void Deallocate_RestoresPriorRegisters()
    {
        var engine = new Engine();
        engine.SetCp(42);
        engine.Allocate(2);

        int stackTopBefore = engine.StackTop;
        engine.Deallocate();

        Assert.Equal(-1, engine.E);
        Assert.Equal(42, engine.Cp);
        // Per the WAM convention, _stackTop is NOT reduced.
        Assert.Equal(stackTopBefore, engine.StackTop);
    }

    [Fact]
    public void Deallocate_NestedFrames_PopsOneAtATime()
    {
        // Allocate captures the *current* _cp into the new frame, so each Deallocate
        // restores _cp to the value that was current at its corresponding Allocate.
        var engine = new Engine();
        engine.SetCp(7);
        engine.Allocate(0);                         // frame 0 saves CP=7
        engine.SetCp(8);
        engine.Allocate(0);                         // frame 2 saves CP=8
        engine.SetCp(9);
        engine.Allocate(0);                         // frame 4 saves CP=9

        engine.Deallocate();                        // pops frame 4 → CP=9, E=2
        Assert.Equal(2, engine.E);
        Assert.Equal(9, engine.Cp);

        engine.Deallocate();                        // pops frame 2 → CP=8, E=0
        Assert.Equal(0, engine.E);
        Assert.Equal(8, engine.Cp);

        engine.Deallocate();                        // pops frame 0 → CP=7, E=-1
        Assert.Equal(-1, engine.E);
        Assert.Equal(7, engine.Cp);
    }

    [Fact]
    public void Deallocate_NoFrame_Throws()
    {
        var engine = new Engine();
        Assert.Throws<InvalidOperationException>(() => engine.Deallocate());
    }

    [Fact]
    public void AllocateDeallocate_RoundTripsRegistersInWamConvention()
    {
        // The WAM convention is allocate ... deallocate proceed, where deallocate restores
        // CE/CP and proceed jumps to CP. The pair must be transparent to the caller.
        var engine = new Engine();
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
        var engine = new Engine();
        engine.Allocate(3);
        engine.SetY(1, Cell.Atom(42));
        Assert.Equal(Cell.Atom(42), engine.GetY(1));
        // The other two slots still hold their initial markers.
        Assert.Equal(Cell.UnboundVar(0 + Engine.EnvY1Offset + 0), engine.GetY(0));
        Assert.Equal(Cell.UnboundVar(0 + Engine.EnvY1Offset + 2), engine.GetY(2));
    }

    [Fact]
    public void GetY_NoActiveFrame_Throws()
    {
        var engine = new Engine();
        Assert.Throws<InvalidOperationException>(() => engine.GetY(0));
    }

    [Fact]
    public void SetY_NoActiveFrame_Throws()
    {
        var engine = new Engine();
        Assert.Throws<InvalidOperationException>(() => engine.SetY(0, Cell.Atom(0)));
    }

    [Fact]
    public void GetY_AfterFrameSwitch_TargetsTheCurrentFrame()
    {
        var engine = new Engine();
        engine.Allocate(1);
        engine.SetY(0, Cell.Atom(10));     // outer Y0 = 10

        engine.Allocate(1);
        engine.SetY(0, Cell.Atom(20));     // inner Y0 = 20
        Assert.Equal(Cell.Atom(20), engine.GetY(0));

        engine.Deallocate();                // back to outer
        Assert.Equal(Cell.Atom(10), engine.GetY(0));
    }
}
