using System.Runtime.InteropServices;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using WebAssembly;
using WebAssembly.Runtime;

namespace Shumway.Tests.Wasm;

/// <summary>The exports a compiled predicate has: one entry point, taking the
/// mailbox address and the continuation cursor, returning a
/// <see cref="WasmVerdict"/>. The method is spelled the way the wire is.
/// </summary>
public abstract class WasmPredicateExports
{
    public abstract int run(int mailbox, int cursor);
}

/// <summary>Runs a compiled predicate on the desktop, with no browser in
/// sight. The memory the module imports is an ordinary block this harness
/// owns, and the engine's arrays are laid out inside it at fixed offsets:
/// that is the whole trick, because the module only ever addresses things by
/// offset into the memory it was given. In the browser that memory happens to
/// be the runtime's own, and nothing about the module changes.</summary>
public sealed class WasmSpikeHarness : IDisposable
{
    /// <summary>Where the mailbox sits. Nothing forces zero; a real engine
    /// hands over the address of its pinned mailbox.</summary>
    public const int MailboxAt = 0;
    /// <summary>Where the register file sits, after the mailbox.</summary>
    public const int RegistersAt = 256;
    /// <summary>Where a heap would sit. This predicate never touches it.</summary>
    public const int HeapAt = 4096;

    private readonly UnmanagedMemory _memory;
    private readonly Instance<WasmPredicateExports> _instance;

    public WasmSpikeHarness(byte[] moduleBytes)
    {
        _memory = new UnmanagedMemory(1, 1);
        using var stream = new MemoryStream(moduleBytes);
        var creator = Module.ReadFromBinary(stream).Compile<WasmPredicateExports>();
        var imports = new ImportDictionary
        {
            { WasmAbi.MemoryModule, WasmAbi.MemoryField, new MemoryImport(() => _memory) },
        };
        _instance = creator(imports);

        SetSlot(WasmAbi.RegistersBase, RegistersAt);
        SetSlot(WasmAbi.HeapBase, HeapAt);
        // A watermark no counter can reach, so the back edge only bails when a
        // test asks it to.
        SetSlot(WasmAbi.HeapWatermark, long.MaxValue);
    }

    /// <summary>Calls the predicate and returns what it decided.</summary>
    public WasmVerdict Run(int cursor = 0)
        => (WasmVerdict)_instance.Exports.run(MailboxAt, cursor);

    public long GetSlot(int slot)
        => Marshal.ReadInt64(_memory.Start, MailboxAt + slot * WasmAbi.SlotSize);

    public void SetSlot(int slot, long value)
        => Marshal.WriteInt64(_memory.Start, MailboxAt + slot * WasmAbi.SlotSize, value);

    public Cell GetRegister(int index)
        => new(Marshal.ReadInt64(_memory.Start, RegistersAt + index * 8));

    public void SetRegister(int index, Cell value)
        => Marshal.WriteInt64(_memory.Start, RegistersAt + index * 8, value.Data);

    public void Dispose()
    {
        _instance.Dispose();
        _memory.Dispose();
    }
}
