using System.Runtime.InteropServices;
using Shumway.Core;
using WebAssembly;
using WebAssembly.Runtime;

namespace Shumway.Compiler.Wasm;

/// <summary>The exports shape of a compiled group module. The method is
/// spelled the way the wire is.</summary>
public abstract class WasmRunExports
{
    public abstract int run(int mailbox, int cursor);
}

/// <summary>The desktop execution world: ONE group module instantiated
/// against a private linear memory (the emitter library's wasm-to-IL
/// engine); a chain copies the engine areas into that image once, runs any
/// number of in-image hops, and copies back at the end. Everything in a cell
/// is an INDEX into its area, never an address, which is what makes the copy
/// model sound. This is the differential-testing world -- the browser pins
/// the real arrays and pays no copies. Engine-thread only.</summary>
public sealed class DesktopWasmWorld : IWasmExecutionWorld, IDisposable
{
    private const int MailboxAt = 1024;
    private const int RegistersAt = 2048;
    private const int Pages = 512;              // 32 MB: generous for tests

    private readonly UnmanagedMemory _memory = new(Pages, Pages);
    private Build? _current;
    private int _functorSynced;
    private int _functorAt = -1;

    /// <summary>One installed group compile: the instance and the maps a
    /// chain captures. Old builds stay referenced by their open chains.</summary>
    private sealed record Build(
        Instance<WasmRunExports> Instance,
        IReadOnlyDictionary<int, int> EntryCursorByFid,
        IReadOnlyDictionary<int, int> CursorByAddress,
        IReadOnlyDictionary<int, int> EntryAddressByFid,
        int RegisterDemand,
        Shumway.Core.WasmBuildAddressIndex AddrIndex);

    // (fid -> live linked address) after a relink; null until one happens.
    // Reference-swapped at a boundary tick, read lock-free by chains.
    private volatile IReadOnlyDictionary<int, int>? _liveByFid;

    public void RefreshLiveAddresses(IReadOnlyDictionary<int, int> liveByFid)
        => _liveByFid = liveByFid;

    public int LiveEntryAddressOf(int functorId)
        => _liveByFid is { } live && live.TryGetValue(functorId, out int at)
            ? at : EntryAddressOf(functorId);

    public long TranslatePcToLive(long buildPc)
        => _current is { } b ? b.AddrIndex.Translate(buildPc, _liveByFid) : buildPc;

    public void InstallGroup(byte[] module,
        IReadOnlyDictionary<int, int> entryCursorByFid,
        IReadOnlyDictionary<int, int> cursorByAddress,
        IReadOnlyDictionary<int, int> entryAddressByFid,
        int registerDemand)
    {
        using var stream = new MemoryStream(module);
        var creator = Module.ReadFromBinary(stream).Compile<WasmRunExports>();
        var instance = creator(new ImportDictionary
        {
            { WasmAbi.MemoryModule, WasmAbi.MemoryField, new MemoryImport(() => _memory) },
        });
        _current = new Build(instance, entryCursorByFid, cursorByAddress,
                             entryAddressByFid, registerDemand,
                             new Shumway.Core.WasmBuildAddressIndex(entryAddressByFid));
    }

    public bool Contains(int functorId)
        => _current?.EntryCursorByFid.ContainsKey(functorId) == true;

    public bool TryResolve(int functorId, int address, out int cursor)
        => TryResolveIn(_current, functorId, address, out cursor);

    private static bool TryResolveIn(Build? b, int functorId, int address, out int cursor)
    {
        cursor = 0;
        if (b is null) return false;
        if (address == 0) return b.EntryCursorByFid.TryGetValue(functorId, out cursor);
        return b.EntryCursorByFid.ContainsKey(functorId)
            && b.CursorByAddress.TryGetValue(address, out cursor);
    }

    public int EntryAddressOf(int functorId)
        => _current!.EntryAddressByFid[functorId];

    public IWasmChainContext BeginChain(Activation engine)
        => new Chain(this, _current ?? throw new InvalidOperationException("no group installed"),
                     engine);

    public void Dispose() => _memory.Dispose();

    /// <summary>Diagnostic: read a mailbox slot from outside the chain — the
    /// only way to see where a hung module got to.</summary>
    public void DebugWriteSlot(int slot, long value)
        => System.Runtime.InteropServices.Marshal.WriteInt64(
            _memory.Start, MailboxAt + slot * 8, value);

    /// <summary>Diagnostic: read a heap cell from the world's IMAGE (what
    /// the module last saw or wrote), from outside any chain.</summary>
    public long DebugReadHeapCell(int index)
    {
        long hb = DebugReadSlot(WasmAbi.HeapBase);
        return System.Runtime.InteropServices.Marshal.ReadInt64(
            _memory.Start + (int)hb + index * 8);
    }

    public long DebugReadSlot(int slot)
        => System.Runtime.InteropServices.Marshal.ReadInt64(
            _memory.Start, MailboxAt + slot * 8);

    private sealed class Chain : IWasmChainContext
    {
        private readonly DesktopWasmWorld _w;
        private readonly Build _build;
        private readonly Activation _engine;
        private readonly long[] _mailbox = new long[WasmAbi.SlotCount];
        private int _heapAt, _stackAt, _trailAt, _functorAt;
        // Exactly one side is authoritative: the image (false) or the engine
        // (true, after SyncEngine ran and managed code may have mutated).
        private bool _engineAuthoritative;

        public Chain(DesktopWasmWorld w, Build build, Activation engine)
        {
            _w = w;
            _build = build;
            _engine = engine;
            StageFromEngine();
        }

        private unsafe void StageFromEngine()
        {
            _engine.EnsureWasmRegisters(_build.RegisterDemand);
            Cell[] heap = _engine.WasmHeapView;
            Cell[] stack = _engine.WasmStackView;
            Cell[] regs = _engine.WasmRegistersView;
            int[] trail = _engine.WasmBindingTrailView;

            _heapAt = RegistersAt + regs.Length * 8;
            _stackAt = _heapAt + heap.Length * 8;
            _trailAt = _stackAt + stack.Length * 8;
            _functorAt = _trailAt + trail.Length * 4;
            int fcount = FunctorTable.IdLimit;
            if (_functorAt + (long)fcount * 8 > (long)Pages * 65536)
                throw new InvalidOperationException("engine areas outgrew the desktop image");
            if (_functorAt != _w._functorAt) { _w._functorAt = _functorAt; _w._functorSynced = 0; }

            var bases = new Activation.WasmMailboxBases(
                _heapAt, _stackAt, RegistersAt, _trailAt,
                HeapLimitCells: heap.Length - 8,
                StackLimitCells: stack.Length - 8,
                TrailLimitEntries: trail.Length - 8,
                FunctorTableBase: _functorAt);
            if (!_engine.TryFillWasmMailbox(_mailbox, bases))
                throw new InvalidOperationException(
                    "a mode-incompatible activation reached the wasm world");

            byte* mem = (byte*)_w._memory.Start;
            fixed (long* p = _mailbox)
                Buffer.MemoryCopy(p, mem + MailboxAt, WasmAbi.SlotCount * 8, WasmAbi.SlotCount * 8);
            fixed (Cell* p = regs)
                Buffer.MemoryCopy(p, mem + RegistersAt, regs.Length * 8L, regs.Length * 8L);
            int h = _engine.HeapTop;
            fixed (Cell* p = heap)
                Buffer.MemoryCopy(p, mem + _heapAt, h * 8L, h * 8L);
            int st = _engine.StackTop;
            fixed (Cell* p = stack)
                Buffer.MemoryCopy(p, mem + _stackAt, st * 8L, st * 8L);
            int tr = _engine.BindingTrailTop;
            fixed (int* p = trail)
                Buffer.MemoryCopy(p, mem + _trailAt, tr * 4L, tr * 4L);
            // An exact copy of the table's own packed array, resumed where
            // the last staging stopped. CopyPackedFrom stops at an id a
            // racing intern has not published, so the mirror never freezes a
            // filler in place; the next staging picks it up.
            if (_w._functorSynced < fcount)
            {
                var dest = new Span<long>(
                    mem + _functorAt + _w._functorSynced * 8L, fcount - _w._functorSynced);
                _w._functorSynced = FunctorTable.CopyPackedFrom(_w._functorSynced, dest);
            }
            _engineAuthoritative = false;
        }

        public WasmVerdict Call(int cursor)
            => (WasmVerdict)_build.Instance.Exports.run(MailboxAt, cursor);

        public bool TryResolve(int functorId, int address, out int cursor)
            => TryResolveIn(_build, functorId, address, out cursor);

        public long TranslatePcToLive(long buildPc)
            => _build.AddrIndex.Translate(buildPc, _w._liveByFid);

        public long ReadSlot(int slot)
            => Marshal.ReadInt64(_w._memory.Start, MailboxAt + slot * WasmAbi.SlotSize);

        public unsafe void SyncEngine()
        {
            if (_engineAuthoritative) return;
            byte* mem = (byte*)_w._memory.Start;
            fixed (long* p = _mailbox)
                Buffer.MemoryCopy(mem + MailboxAt, p, WasmAbi.SlotCount * 8, WasmAbi.SlotCount * 8);
            Cell[] heap = _engine.WasmHeapView;
            Cell[] stack = _engine.WasmStackView;
            Cell[] regs = _engine.WasmRegistersView;
            int[] trail = _engine.WasmBindingTrailView;
            // Live data ends at the synced tops: a binding always lands below
            // the area's top at bind time, and anything above the FINAL top is
            // dead (unwound or deallocated).
            int h2 = (int)_mailbox[WasmAbi.HeapTop];
            fixed (Cell* p = heap)
                Buffer.MemoryCopy(mem + _heapAt, p, heap.Length * 8L, h2 * 8L);
            int st2 = (int)_mailbox[WasmAbi.StackTop];
            fixed (Cell* p = stack)
                Buffer.MemoryCopy(mem + _stackAt, p, stack.Length * 8L, st2 * 8L);
            fixed (Cell* p = regs)
                Buffer.MemoryCopy(mem + RegistersAt, p, regs.Length * 8L, regs.Length * 8L);
            int tr2 = (int)_mailbox[WasmAbi.TrailTop];
            fixed (int* p = trail)
                Buffer.MemoryCopy(mem + _trailAt, p, trail.Length * 4L, tr2 * 4L);
            _engine.SyncFromWasmMailbox(_mailbox);
            _engineAuthoritative = true;
        }

        public void RefreshFromEngine()
        {
            if (!_engineAuthoritative)
                throw new InvalidOperationException("refresh without a preceding sync");
            StageFromEngine();
        }

        public void Dispose() => SyncEngine();
    }
}
