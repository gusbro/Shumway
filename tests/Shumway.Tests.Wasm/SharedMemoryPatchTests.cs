using Shumway.Compiler.Wasm;

using Shumway.Core;

namespace Shumway.Tests.Wasm;

/// <summary>The browser will not instantiate a module whose memory import is
/// not marked shared against a shared memory, and the emitter has no flag for
/// it. The plan (D5) says to set the bit where it lives, in the limits byte of
/// the import section, and these hold that to its word: one byte changes, the
/// module still reads back, and everything else is identical.</summary>
public class SharedMemoryPatchTests
{
    [Fact]
    public void ThePatchChangesOneByte()
    {
        byte[] plain = SpikeCounterModule.ToBytes();
        byte[] shared = SpikeCounterModule.ToBytes(shared: true);

        Assert.Equal(plain.Length, shared.Length);
        int[] differing = Enumerable.Range(0, plain.Length)
                                    .Where(i => plain[i] != shared[i])
                                    .ToArray();
        Assert.Single(differing);
        Assert.Equal(0x01, plain[differing[0]]);      // minimum and maximum
        Assert.Equal(0x03, shared[differing[0]]);     // ...and shared
    }

    [Fact]
    public void ItSaysWhichOneItIs()
    {
        Assert.False(WasmSharedMemory.IsShared(SpikeCounterModule.ToBytes()));
        Assert.True(WasmSharedMemory.IsShared(SpikeCounterModule.ToBytes(shared: true)));
    }

    [Fact]
    public void APatchedModuleStillReadsBack()
    {
        // The reader is the library's validator: if the byte had landed
        // anywhere else, the sections after it would not parse.
        using var stream = new MemoryStream(SpikeCounterModule.ToBytes(shared: true));
        var module = WebAssembly.Module.ReadFromBinary(stream);

        Assert.Single(module.Imports);
        Assert.Single(module.Exports);
        Assert.Equal(WasmAbi.EntryExport, module.Exports[0].Name);
    }

    [Fact]
    public void PatchingTwiceIsRefused()
    {
        // Not idempotent by design: a second patch would mean the first one
        // found something other than what it thought it did.
        byte[] shared = SpikeCounterModule.ToBytes(shared: true);
        Assert.Throws<InvalidOperationException>(() => WasmSharedMemory.Patch(shared));
    }
}
