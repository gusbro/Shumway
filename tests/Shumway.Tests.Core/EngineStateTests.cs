using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class EngineStateTests
{
    [Fact]
    public void Default_Ctor_StartsEmpty()
    {
        var engine = new Activation();
        Assert.Equal(0, engine.HeapTop);
        Assert.Equal(0, engine.Hb);
        Assert.Equal(0, engine.StackTop);
        Assert.Equal(0, engine.BindingTrailTop);
        Assert.Equal(0, engine.ExtraTrailTop);
        Assert.Equal(-1, engine.E);
        Assert.Equal(-1, engine.B);
        Assert.Equal(-1, engine.P);
        Assert.Equal(-1, engine.Cp);
    }

    [Fact]
    public void Default_Ctor_UsesDefaultConfigSizes()
    {
        var engine = new Activation();
        var defaults = new ActivationConfig();
        Assert.Equal(defaults.InitialHeapSize, engine.HeapCapacity);
        Assert.Equal(defaults.InitialStackSize, engine.StackCapacity);
        Assert.Equal(defaults.InitialRegisterCount, engine.RegisterCount);
        Assert.Equal(defaults.InitialBindingTrailSize, engine.BindingTrailCapacity);
        Assert.Equal(defaults.InitialExtraTrailSize, engine.ExtraTrailCapacity);
    }

    [Fact]
    public void Custom_Config_AppliesInitialSizes()
    {
        var config = new ActivationConfig
        {
            InitialHeapSize = 7,
            InitialStackSize = 9,
            InitialRegisterCount = 3,
            InitialBindingTrailSize = 5,
            InitialExtraTrailSize = 2,
        };
        var engine = new Activation(config);
        Assert.Equal(7, engine.HeapCapacity);
        Assert.Equal(9, engine.StackCapacity);
        Assert.Equal(3, engine.RegisterCount);
        Assert.Equal(5, engine.BindingTrailCapacity);
        Assert.Equal(2, engine.ExtraTrailCapacity);
    }

    [Fact]
    public void Ctor_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new Activation(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_InvalidConfigSize_Throws(int size)
    {
        Assert.Throws<ArgumentException>(() => new Activation(new ActivationConfig { InitialHeapSize = size }));
    }

    [Fact]
    public void AllocateHeapUnbound_AdvancesTopAndWritesSelfPointingRef()
    {
        var engine = new Activation();
        int a = engine.AllocateHeapUnbound();
        Assert.Equal(0, a);
        Assert.Equal(1, engine.HeapTop);

        var cell = engine.GetHeap(a);
        Assert.Equal(Tag.Ref, cell.Tag);
        Assert.Equal(a, cell.AsHeapIndex);

        int b = engine.AllocateHeapUnbound();
        Assert.Equal(1, b);
        Assert.Equal(2, engine.HeapTop);
    }

    [Fact]
    public void AllocateHeap_AdvancesTopByCount()
    {
        var engine = new Activation();
        int a = engine.AllocateHeap(3);
        Assert.Equal(0, a);
        Assert.Equal(3, engine.HeapTop);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AllocateHeap_NonPositiveCount_Throws(int count)
    {
        var engine = new Activation();
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.AllocateHeap(count));
    }

    [Fact]
    public void Heap_GrowsGeometricallyWhenExhausted()
    {
        var engine = new Activation(new ActivationConfig { InitialHeapSize = 2 });
        Assert.Equal(2, engine.HeapCapacity);

        engine.AllocateHeapUnbound();
        engine.AllocateHeapUnbound();
        Assert.Equal(2, engine.HeapCapacity);   // still at initial

        engine.AllocateHeapUnbound();           // forces growth
        Assert.True(engine.HeapCapacity >= 3, $"capacity {engine.HeapCapacity} did not grow");
        Assert.True(engine.HeapCapacity >= 4, "geometric growth expected to double");
    }

    [Fact]
    public void Heap_PreservesContentsAcrossGrowth()
    {
        var engine = new Activation(new ActivationConfig { InitialHeapSize = 2 });
        int a = engine.AllocateHeap(1);
        engine.SetHeap(a, Cell.Atom(7));

        // Force several growth rounds.
        for (int i = 0; i < 100; i++)
            engine.AllocateHeapUnbound();

        Assert.Equal(Cell.Atom(7), engine.GetHeap(a));
    }

    [Fact]
    public void Heap_MaxSizeExceeded_Throws()
    {
        var engine = new Activation(new ActivationConfig { InitialHeapSize = 2, MaxHeapSize = 4 });
        engine.AllocateHeap(4); // exactly fills the max
        Assert.Equal(4, engine.HeapTop);

        var ex = Assert.Throws<PrologRuntimeException>(() => engine.AllocateHeapUnbound());
        Assert.Equal("resource_error", ex.Kind);   // catchable: error(resource_error(memory), _)
    }

    [Fact]
    public void Heap_MaxSizeZero_MeansUnlimited()
    {
        var engine = new Activation(new ActivationConfig { InitialHeapSize = 2, MaxHeapSize = 0 });
        // Growing past the initial budget should not throw.
        for (int i = 0; i < 50; i++)
            engine.AllocateHeapUnbound();
        Assert.Equal(50, engine.HeapTop);
    }

    [Fact]
    public void SetHbForTesting_InRange_Updates()
    {
        var engine = new Activation();
        engine.AllocateHeap(5);
        engine.SetHbForTesting(3);
        Assert.Equal(3, engine.Hb);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]   // beyond heap top
    public void SetHbForTesting_OutOfRange_Throws(int badHb)
    {
        var engine = new Activation();
        engine.AllocateHeap(5);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetHbForTesting(badHb));
    }

    // ADR-035 — the breakpoint Break-byte decoder. The live table decodes a Break; a miss means
    // the code space and the table drifted (a bug), so it fails loudly rather than run on.

    [Fact]
    public void BreakpointOriginalAt_ReturnsTheRecordedOriginal()
    {
        var engine = new Activation
        {
            BreakpointOriginals = new System.Collections.Generic.Dictionary<int, byte> { [0x100] = 0x2A },
        };
        Assert.Equal(0x2A, engine.BreakpointOriginalAt(0x100));
    }

    [Fact]
    public void BreakpointOriginalAt_ThrowsWhenTheTableHasNoEntry()
    {
        // A Break byte at a pc with no table entry is drift/corruption — it fails loudly rather
        // than dispatch a wrong opcode. The debug service prevents this by always un-patching the
        // buffer the activation actually runs, so a removed breakpoint leaves no orphan Break.
        var engine = new Activation
        {
            BreakpointOriginals = new System.Collections.Generic.Dictionary<int, byte>(),
        };
        Assert.Throws<System.InvalidOperationException>(() => engine.BreakpointOriginalAt(0x739C));
    }
}
