using System.Runtime.InteropServices;
using Shumway.Core;
using WebAssembly;
using WebAssembly.Runtime;

namespace Shumway.Compiler.Wasm;

/// <summary>The exports shape of a compiled predicate module. The method is
/// spelled the way the wire is.</summary>
public abstract class WasmRunExports
{
    public abstract int run(int mailbox, int cursor);
}

/// <summary>Runs a compiled predicate against a LIVE activation on the
/// desktop, through the emitter library's wasm-to-IL engine: the engine
/// areas are copied into a private linear-memory image around every entry
/// and copied back after. Everything in a cell is an INDEX into its area,
/// never an address, which is what makes the copy model sound. This is the
/// differential-testing runner -- the browser pins the real arrays instead
/// and pays no copies.</summary>
public sealed class DesktopWasmRunner : IWasmActivationRunner
{
    private const int MailboxAt = 1024;
    private const int RegistersAt = 2048;
    private const int Pages = 512;              // 32 MB: generous for tests

    private readonly UnmanagedMemory _memory;
    private readonly Instance<WasmRunExports> _instance;
    private readonly long[] _mailbox = new long[WasmAbi.SlotCount];
    private int _aritySynced;
    private int _arityAt = -1;

    public DesktopWasmRunner(byte[] module)
    {
        _memory = new UnmanagedMemory(Pages, Pages);
        using var stream = new MemoryStream(module);
        var creator = Module.ReadFromBinary(stream).Compile<WasmRunExports>();
        _instance = creator(new ImportDictionary
        {
            { WasmAbi.MemoryModule, WasmAbi.MemoryField, new MemoryImport(() => _memory) },
        });
    }

    public unsafe WasmVerdict Run(Activation engine, int cursor)
    {
        Cell[] heap = engine.WasmHeapView;
        Cell[] stack = engine.WasmStackView;
        Cell[] regs = engine.WasmRegistersView;
        int[] trail = engine.WasmBindingTrailView;

        int heapAt = RegistersAt + regs.Length * 8;
        int stackAt = heapAt + heap.Length * 8;
        int trailAt = stackAt + stack.Length * 8;
        int arityAt = trailAt + trail.Length * 4;
        int fcount = FunctorTable.Count;
        if (arityAt + (long)fcount * 4 > (long)Pages * 65536)
            throw new InvalidOperationException("engine areas outgrew the desktop runner image");
        if (arityAt != _arityAt) { _arityAt = arityAt; _aritySynced = 0; }

        var bases = new Activation.WasmMailboxBases(
            heapAt, stackAt, RegistersAt, trailAt,
            HeapLimitCells: heap.Length - 8,
            StackLimitCells: stack.Length - 8,
            TrailLimitEntries: trail.Length - 8,
            FunctorArityBase: arityAt);
        if (!engine.TryFillWasmMailbox(_mailbox, bases))
            throw new InvalidOperationException(
                "a mode-incompatible activation reached the wasm runner");

        byte* mem = (byte*)_memory.Start;
        fixed (long* p = _mailbox)
            Buffer.MemoryCopy(p, mem + MailboxAt, WasmAbi.SlotCount * 8, WasmAbi.SlotCount * 8);
        fixed (Cell* p = regs)
            Buffer.MemoryCopy(p, mem + RegistersAt, regs.Length * 8L, regs.Length * 8L);
        int h = engine.HeapTop;
        fixed (Cell* p = heap)
            Buffer.MemoryCopy(p, mem + heapAt, h * 8L, h * 8L);
        int st = engine.StackTop;
        fixed (Cell* p = stack)
            Buffer.MemoryCopy(p, mem + stackAt, st * 8L, st * 8L);
        int tr = engine.BindingTrailTop;
        fixed (int* p = trail)
            Buffer.MemoryCopy(p, mem + trailAt, tr * 4L, tr * 4L);
        // TryLookup, not Lookup: the id space can have holes (atom GC) and
        // ids other threads interned but not yet published. Neither kind can
        // appear in THIS engine's areas, so 0 is a safe filler.
        for (; _aritySynced < fcount; _aritySynced++)
            *(int*)(mem + arityAt + _aritySynced * 4L)
                = FunctorTable.TryLookup(_aritySynced, out var fe) ? fe.Arity : 0;

        int verdict = _instance.Exports.run(MailboxAt, cursor);

        fixed (long* p = _mailbox)
            Buffer.MemoryCopy(mem + MailboxAt, p, WasmAbi.SlotCount * 8, WasmAbi.SlotCount * 8);
        // Live data ends at the synced tops: a binding always lands below the
        // area's top at bind time, and anything above the FINAL top is dead
        // (unwound or deallocated), so the final tops bound the copy-back.
        int h2 = (int)_mailbox[WasmAbi.HeapTop];
        fixed (Cell* p = heap)
            Buffer.MemoryCopy(mem + heapAt, p, heap.Length * 8L, h2 * 8L);
        int st2 = (int)_mailbox[WasmAbi.StackTop];
        fixed (Cell* p = stack)
            Buffer.MemoryCopy(mem + stackAt, p, stack.Length * 8L, st2 * 8L);
        fixed (Cell* p = regs)
            Buffer.MemoryCopy(mem + RegistersAt, p, regs.Length * 8L, regs.Length * 8L);
        int tr2 = (int)_mailbox[WasmAbi.TrailTop];
        fixed (int* p = trail)
            Buffer.MemoryCopy(mem + trailAt, p, trail.Length * 4L, tr2 * 4L);

        engine.SyncFromWasmMailbox(_mailbox);
        return (WasmVerdict)verdict;
    }

    public long ReadSlot(int slot) => _mailbox[slot];

    public void Dispose() => _memory.Dispose();
}
