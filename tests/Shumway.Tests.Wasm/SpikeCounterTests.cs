using Shumway.Compiler.Wasm;
using Shumway.Core;

namespace Shumway.Tests.Wasm;

/// <summary>Phase 0 of the wasm Tier-1 arc, desktop half: the hand-built
/// counter module, executed without a browser.
///
/// <para>What these pin is the ABI, not the speed. The module reaches the
/// engine's registers through the mailbox and nothing else, unboxes and
/// reboxes a cell exactly as <see cref="Cell"/> defines it, comes out at a
/// safe point when the engine asks and goes back in where it left off. If any
/// of that is wrong, the browser measurements would be measuring the wrong
/// thing.</para></summary>
public class SpikeCounterTests
{
    private static WasmSpikeHarness Harness() => new(SpikeCounterModule.ToBytes());

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100000)]
    public void ItCountsDownToZero(long start)
    {
        using var h = Harness();
        h.SetRegister(0, Cell.Int(start));

        Assert.Equal(WasmVerdict.Success, h.Run());
        Assert.Equal(Tag.Int, h.GetRegister(0).Tag);
        Assert.Equal(0, h.GetRegister(0).AsInt);
    }

    [Fact]
    public void ACounterAlreadyPastZeroIsLeftAlone()
    {
        // n =< 0 leaves the loop at once, and what it writes back is what it
        // read: the sign survives the unbox and the rebox.
        using var h = Harness();
        h.SetRegister(0, Cell.Int(-5));

        Assert.Equal(WasmVerdict.Success, h.Run());
        Assert.Equal(-5, h.GetRegister(0).AsInt);
    }

    [Fact]
    public void ItReadsTheRegistersThroughTheMailbox()
    {
        // Move the register file and the predicate follows: the base is read
        // from the mailbox on entry, which is what makes a fresh base per
        // entry possible at all (the plan's D2).
        using var h = Harness();
        h.SetSlot(WasmAbi.RegistersBase, WasmSpikeHarness.HeapAt);
        h.SetRegister(0, Cell.Int(3));               // the OLD place, untouched

        // X0 at the new base is whatever the memory held there: zero, which is
        // not an integer cell, so the predicate refuses it.
        Assert.Equal(WasmVerdict.Fail, h.Run());
        Assert.Equal(3, h.GetRegister(0).AsInt);
    }

    [Fact]
    public void WhatIsNotAnIntegerIsRefused()
    {
        using var h = Harness();
        h.SetRegister(0, Cell.Atom(7));

        Assert.Equal(WasmVerdict.Fail, h.Run());
        // ...and it did not write anything back over it.
        Assert.Equal(Tag.Atom, h.GetRegister(0).Tag);
    }

    [Fact]
    public void AFlagOnTheMailboxBringsItOutAtTheBackEdge()
    {
        // ADR-049's concern in one test: a loop that ignores the flags word
        // swallows a wakeup, an interrupt and a cancellation alike. This one
        // comes out on the first back edge, having done exactly one round.
        using var h = Harness();
        h.SetRegister(0, Cell.Int(1000));
        h.SetSlot(WasmAbi.Flags, WasmAbi.FlagWakeupPending);

        Assert.Equal(WasmVerdict.Safepoint, h.Run());
        Assert.Equal(999, h.GetRegister(0).AsInt);
        Assert.Equal(SpikeCounterModule.ResumeCursor, h.GetSlot(WasmAbi.Cursor));
    }

    [Fact]
    public void AndItGoesBackInWhereItLeftOff()
    {
        using var h = Harness();
        h.SetRegister(0, Cell.Int(1000));
        h.SetSlot(WasmAbi.Flags, WasmAbi.FlagWakeupPending);
        Assert.Equal(WasmVerdict.Safepoint, h.Run());

        // The wrapper does whatever the flag asked for, clears it, and
        // re-enters at the cursor the module left.
        h.SetSlot(WasmAbi.Flags, 0);
        Assert.Equal(WasmVerdict.Success, h.Run((int)h.GetSlot(WasmAbi.Cursor)));
        Assert.Equal(0, h.GetRegister(0).AsInt);
    }

    [Fact]
    public void TheHeapWatermarkBringsItOutToo()
    {
        // The other half of the back-edge check: the engine says "the heap has
        // gone far enough", and the loop stops so a collection can happen in
        // managed code, where the arrays it would move are visible.
        using var h = Harness();
        h.SetRegister(0, Cell.Int(50));
        h.SetSlot(WasmAbi.HeapTop, 900);
        h.SetSlot(WasmAbi.HeapWatermark, 900);

        Assert.Equal(WasmVerdict.Safepoint, h.Run());
        Assert.Equal(49, h.GetRegister(0).AsInt);

        // Collected: the top is back below the mark, and the rest runs.
        h.SetSlot(WasmAbi.HeapTop, 100);
        Assert.Equal(WasmVerdict.Success, h.Run((int)h.GetSlot(WasmAbi.Cursor)));
        Assert.Equal(0, h.GetRegister(0).AsInt);
    }

    [Fact]
    public void ASuccessLeavesNoCursorBehind()
    {
        using var h = Harness();
        h.SetRegister(0, Cell.Int(3));
        h.SetSlot(WasmAbi.Cursor, 99);

        Assert.Equal(WasmVerdict.Success, h.Run());
        Assert.Equal(0, h.GetSlot(WasmAbi.Cursor));
    }

    [Fact]
    public void TheModuleValidates()
    {
        // Round-tripping through the reader is the library's validation pass:
        // types, block depths, stack shape. A hand-built body that does not
        // validate would fail in the browser with far less to go on.
        byte[] bytes = SpikeCounterModule.ToBytes();
        using var stream = new MemoryStream(bytes);
        var back = WebAssembly.Module.ReadFromBinary(stream);

        Assert.Single(back.Exports);
        Assert.Equal(WasmAbi.EntryExport, back.Exports[0].Name);
        Assert.Single(back.Imports);
        Assert.Empty(back.Memories);          // imported, never defined
    }
}
