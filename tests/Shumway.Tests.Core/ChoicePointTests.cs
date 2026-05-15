using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class ChoicePointTests
{
    // ---------- Layout ----------

    [Fact]
    public void CpLayoutConstants_MatchAdr005()
    {
        Assert.Equal(0, Engine.CpArityOffset);
        Assert.Equal(1, Engine.CpArg1Offset);
        Assert.Equal(9, Engine.CpSize(0));
        Assert.Equal(9 + 5, Engine.CpSize(5));

        // For arity N: offsets after the args (1 + N) advance by 1 each.
        Assert.Equal(1 + 2, Engine.CpCeOffset(2));
        Assert.Equal(1 + 2 + 1, Engine.CpCpOffset(2));
        Assert.Equal(1 + 2 + 2, Engine.CpBOffset(2));
        Assert.Equal(1 + 2 + 3, Engine.CpBpOffset(2));
        Assert.Equal(1 + 2 + 4, Engine.CpBindingTrailOffset(2));
        Assert.Equal(1 + 2 + 5, Engine.CpExtraTrailOffset(2));
        Assert.Equal(1 + 2 + 6, Engine.CpHeapTopOffset(2));
        Assert.Equal(1 + 2 + 7, Engine.CpHbOffset(2));
    }

    // ---------- Push ----------

    [Fact]
    public void Push_FirstCp_SnapshotsStateAndAdvancesB()
    {
        var engine = new Engine();
        engine.SetCpForTesting(0x77);
        engine.Allocate(1);                          // env at idx 0, _stackTop = 3
        int eAfterAllocate = engine.E;
        int stackTopBefore = engine.StackTop;

        engine.SetRegister(0, Cell.Atom(10));
        engine.SetRegister(1, Cell.Atom(20));

        engine.PushChoicePoint(arity: 2, nextClauseAddr: 0x1234);

        int b = engine.B;
        Assert.Equal(stackTopBefore, b);             // newB = previous stackTop
        Assert.Equal(stackTopBefore + Engine.CpSize(2), engine.StackTop);
        Assert.Equal(engine.HeapTop, engine.Hb);     // bumped to current heap top

        Assert.Equal(2L, engine.GetStack(b + Engine.CpArityOffset).Data);
        Assert.Equal(Cell.Atom(10), engine.GetStack(b + Engine.CpArg1Offset));
        Assert.Equal(Cell.Atom(20), engine.GetStack(b + Engine.CpArg1Offset + 1));
        Assert.Equal((long)eAfterAllocate, engine.GetStack(b + Engine.CpCeOffset(2)).Data);
        Assert.Equal(0x77L, engine.GetStack(b + Engine.CpCpOffset(2)).Data);
        Assert.Equal(-1L, engine.GetStack(b + Engine.CpBOffset(2)).Data);
        Assert.Equal(0x1234L, engine.GetStack(b + Engine.CpBpOffset(2)).Data);
    }

    [Fact]
    public void Push_UpdatesHbToCurrentHeapTop()
    {
        var engine = new Engine();
        engine.AllocateHeap(5);
        Assert.Equal(0, engine.Hb);

        engine.PushChoicePoint(0, 0);
        Assert.Equal(5, engine.Hb);
    }

    [Fact]
    public void Push_NestedCps_ChainsB()
    {
        var engine = new Engine();
        engine.PushChoicePoint(0, 0x100);
        int outer = engine.B;
        engine.PushChoicePoint(0, 0x200);
        int inner = engine.B;
        Assert.Equal((long)outer, engine.GetStack(inner + Engine.CpBOffset(0)).Data);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Push_NegativeArity_Throws(int arity)
    {
        var engine = new Engine();
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.PushChoicePoint(arity, 0));
    }

    [Fact]
    public void Push_ArityExceedsRegisters_Throws()
    {
        var engine = new Engine(new EngineConfig { InitialRegisterCount = 2 });
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.PushChoicePoint(3, 0));
    }

    [Fact]
    public void Push_GrowsStackOnOverflow()
    {
        var engine = new Engine(new EngineConfig { InitialStackSize = 4 });
        // CpSize(0) = 9, well past the initial 4 — must grow.
        engine.PushChoicePoint(0, 0);
        Assert.True(engine.StackCapacity >= Engine.CpSize(0));
    }

    // ---------- RetryMeElse ----------

    [Fact]
    public void Retry_RestoresArgRegisters()
    {
        var engine = new Engine();
        engine.SetRegister(0, Cell.Atom(10));
        engine.SetRegister(1, Cell.Atom(20));
        engine.PushChoicePoint(2, 0);

        engine.SetRegister(0, Cell.Atom(99));
        engine.SetRegister(1, Cell.Atom(98));

        engine.RetryMeElse(0);

        Assert.Equal(Cell.Atom(10), engine.GetRegister(0));
        Assert.Equal(Cell.Atom(20), engine.GetRegister(1));
    }

    [Fact]
    public void Retry_RestoresEAndCp()
    {
        var engine = new Engine();
        engine.SetCpForTesting(0x55);
        engine.Allocate(0);
        int eAtPush = engine.E;
        engine.PushChoicePoint(0, 0);

        // Mutate state after the CP push.
        engine.SetCpForTesting(0xAA);
        engine.Allocate(0);
        Assert.NotEqual(eAtPush, engine.E);

        engine.RetryMeElse(0);

        Assert.Equal(eAtPush, engine.E);
        Assert.Equal(0x55, engine.Cp);
    }

    [Fact]
    public void Retry_RestoresHeapTop()
    {
        var engine = new Engine();
        engine.AllocateHeap(5);
        engine.PushChoicePoint(0, 0);
        int heapAtPush = engine.HeapTop;

        engine.AllocateHeap(7);
        engine.RetryMeElse(0);
        Assert.Equal(heapAtPush, engine.HeapTop);
    }

    [Fact]
    public void Retry_SetsHbToRestoredHeapTop()
    {
        // The CP is still active after retry, so its boundary is its own heap top.
        var engine = new Engine();
        engine.AllocateHeap(5);
        engine.PushChoicePoint(0, 0);
        Assert.Equal(5, engine.Hb);

        engine.AllocateHeap(3);                    // _heapTop = 8, _hb still 5
        engine.RetryMeElse(0);
        Assert.Equal(5, engine.HeapTop);
        Assert.Equal(5, engine.Hb);
    }

    [Fact]
    public void Retry_DoesNotChangeB()
    {
        var engine = new Engine();
        engine.PushChoicePoint(0, 0);
        int bBefore = engine.B;
        engine.RetryMeElse(0);
        Assert.Equal(bBefore, engine.B);
    }

    [Fact]
    public void Retry_UpdatesBpToNewAddress()
    {
        var engine = new Engine();
        engine.PushChoicePoint(0, 0x1000);
        int b = engine.B;
        Assert.Equal(0x1000L, engine.GetStack(b + Engine.CpBpOffset(0)).Data);

        engine.RetryMeElse(0x2000);
        Assert.Equal(0x2000L, engine.GetStack(b + Engine.CpBpOffset(0)).Data);
    }

    [Fact]
    public void Retry_RolledBackBindingsAreRestored()
    {
        var engine = new Engine();
        int v = engine.AllocateHeapUnbound();
        engine.PushChoicePoint(0, 0);                // _hb = 1; v at idx 0 is "old"
        engine.Bind(v, Cell.Atom(99));
        Assert.Equal(1, engine.BindingTrailTop);

        engine.RetryMeElse(0);
        Assert.Equal(0, engine.BindingTrailTop);
        Assert.Equal(Cell.UnboundVar(v), engine.GetHeap(v));
    }

    [Fact]
    public void Retry_NoCp_Throws()
    {
        var engine = new Engine();
        Assert.Throws<InvalidOperationException>(() => engine.RetryMeElse(0));
    }

    // ---------- TrustMe ----------

    [Fact]
    public void Trust_RestoresStateAndDiscardsCp()
    {
        var engine = new Engine();
        engine.PushChoicePoint(0, 0);
        int stackAtPush = engine.B;

        engine.TrustMe();
        Assert.Equal(-1, engine.B);
        Assert.Equal(stackAtPush, engine.StackTop);   // CP slots reclaimed
    }

    [Fact]
    public void Trust_RestoresHbToSavedPreCpValue()
    {
        // Saved Hb is the pre-push _hb. Trust returns us to the pre-CP boundary.
        var engine = new Engine();
        engine.AllocateHeap(3);                       // _hb stays 0
        engine.PushChoicePoint(0, 0);                 // saved Hb = 0, _hb = 3
        engine.AllocateHeap(5);

        engine.TrustMe();
        Assert.Equal(3, engine.HeapTop);              // restored
        Assert.Equal(0, engine.Hb);                   // pre-CP value
    }

    [Fact]
    public void Trust_NestedCps_ReturnsToPrevious()
    {
        var engine = new Engine();
        engine.PushChoicePoint(0, 0x100);
        int outer = engine.B;
        engine.PushChoicePoint(0, 0x200);
        int inner = engine.B;

        engine.TrustMe();

        Assert.Equal(outer, engine.B);
        Assert.Equal(inner, engine.StackTop);         // inner CP's space reclaimed
    }

    [Fact]
    public void Trust_NoCp_Throws()
    {
        var engine = new Engine();
        Assert.Throws<InvalidOperationException>(() => engine.TrustMe());
    }

    // ---------- Combined ----------

    [Fact]
    public void RetryThenTrust_AppliesBothCorrectly()
    {
        var engine = new Engine();
        int v = engine.AllocateHeapUnbound();
        engine.PushChoicePoint(0, 0x100);              // _hb = 1, v is "old"

        // First alternative bind, then retry.
        engine.Bind(v, Cell.Atom(1));
        engine.RetryMeElse(0x200);
        Assert.Equal(Cell.UnboundVar(v), engine.GetHeap(v));

        // Second alternative bind, then trust.
        engine.Bind(v, Cell.Atom(2));
        engine.TrustMe();
        Assert.Equal(Cell.UnboundVar(v), engine.GetHeap(v));
        Assert.Equal(-1, engine.B);
    }
}
