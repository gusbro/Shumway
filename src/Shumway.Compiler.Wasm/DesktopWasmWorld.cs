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

/// <summary>The desktop execution world: the engine's modules instantiated
/// against a private linear memory (the emitter library's wasm-to-IL
/// engine); a chain copies the engine areas into that image once, runs any
/// number of in-image hops, and copies back at the end. Everything in a cell
/// is an INDEX into its area, never an address, which is what makes the copy
/// model sound -- and what lets the SAME emitted code run under both
/// staging models. Engine-thread only.
///
/// <para><b>Why this copies and the browser does not, structurally.</b> In
/// the browser the .NET runtime is itself compiled to wasm, so the engine's
/// Cell[] arrays already live INSIDE the linear memory the module imports:
/// pinning one and handing over its address gives the module the engine's
/// real array, and there is nothing to copy because there is only one
/// memory. Here the module runs against a private UnmanagedMemory block
/// (DesktopWasmSpace) while the engine's arrays are managed objects in the
/// CLR heap -- two address spaces -- and wasm code can only address offsets
/// into its own linear memory. Pinning would stop the GC moving an array; it
/// would not move the array inside the block, so it buys nothing here. The
/// copy is not a shortcut this world took, it is the only way across.</para>
///
/// <para>Closing the gap would mean allocating the engine's areas in
/// unmanaged memory inside that block -- surgery on the engine's core (the
/// interpreter and the ADR-016 heap GC both walk those arrays), for the
/// benefit of a test harness. So this world is for CORRECTNESS and for
/// COUNTS, which are identical in both; a TIME taken here measures the
/// copying (see CONTRIBUTING.md, "Measuring the WebAssembly tier").</para></summary>
public sealed class DesktopWasmWorld : IWasmExecutionWorld, IDisposable
{
    private const int MailboxAt = DesktopWasmSpace.MailboxAt;
    private const int RegistersAt = DesktopWasmSpace.RegistersAt;
    private const int Pages = DesktopWasmSpace.Pages;

    private readonly DesktopWasmSpace _space;
    private readonly bool _ownsSpace;
    private UnmanagedMemory _memory => _space.Memory;

    /// <summary>A world of its own engine, in a space of its own.</summary>
    public DesktopWasmWorld() : this(new DesktopWasmSpace(), ownsSpace: true) { }

    /// <summary>A world sharing an engine's space with its siblings: one
    /// memory, one function table, one module set. Two worlds over one space
    /// are two installers of the same modules; the tests use that to stand
    /// in for two promoters. The caller owns the space's lifetime.</summary>
    public DesktopWasmWorld(DesktopWasmSpace space) : this(space, ownsSpace: false) { }

    private DesktopWasmWorld(DesktopWasmSpace space, bool ownsSpace)
    {
        _space = space;
        _ownsSpace = ownsSpace;
    }

    /// <summary>The function table every module of this engine is registered
    /// in. The browser has one per thread already (emscripten's); here it is
    /// made explicitly so the same emitted code works in both.</summary>
    public FunctionTable Functions => _space.Functions;

    /// <summary>The modules and the rows they resolve through, shared with
    /// every world of the same space.</summary>
    public WasmModuleRegistry Modules => _space.Modules;

    public Shumway.Core.WasmResumeTable ResumeTable => _space.ResumeTable;

    // (fid -> live linked address) after a relink; null until one happens.
    // Reference-swapped at a boundary tick, read lock-free by chains.
    private volatile IReadOnlyDictionary<int, int>? _liveByFid;

    public void RefreshLiveAddresses(IReadOnlyDictionary<int, int> liveByFid)
        => _liveByFid = liveByFid;

    public int LiveEntryAddressOf(int functorId)
        => _liveByFid is { } live && live.TryGetValue(functorId, out int at)
            ? at : EntryAddressOf(functorId);

    public long TranslatePcToLive(int functorId, long buildPc)
        => Modules.TranslatePcToLive(functorId, buildPc, _liveByFid);

    /// <summary>The id the next install will get: what a module has to be
    /// compiled against, since the id is baked into its probes.</summary>
    public int NextModuleId => ResumeTable.ModuleCount;

    public IReadOnlyList<int> InstallGroup(byte[] module,
        IReadOnlyDictionary<int, int> entryCursorByFid,
        IReadOnlyDictionary<int, int> cursorByAddress,
        IReadOnlyDictionary<int, int> entryAddressByFid,
        int registerDemand,
        IEnumerable<(int Caller, int Callee)> callEdges)
    {
        using var stream = new MemoryStream(module);
        var creator = Module.ReadFromBinary(stream).Compile<WasmRunExports>();
        var instance = creator(new ImportDictionary
        {
            { WasmAbi.MemoryModule, WasmAbi.MemoryField, new MemoryImport(() => _memory) },
            { WasmAbi.TableModule, WasmAbi.TableField, Functions },
        });
        var m = Modules.Install(entryCursorByFid, cursorByAddress, entryAddressByFid,
                                registerDemand, callEdges, out var displaced);
        // The slot IS the module id: the module index array the hop reads
        // maps i -> i here (the browser's addFunction picks its own).
        while (Functions.Length <= m.Id) Functions.Grow(1);
        Functions[m.Id] = TailEntry(instance.Exports);
        _space.Instances.Add(instance);
        return displaced;
    }

    /// <summary>The table entry for a module: a stub that TAIL-calls the
    /// module's run with an explicit tail. prefix. The library's exported
    /// wrapper is a plain call into the internal function followed by ret;
    /// the JIT turns that into a tail call only opportunistically (not
    /// under MinOpts or a debugger), and a hop through a non-tail wrapper
    /// keeps one frame per hop. The internal function is the library's
    /// "👻 &lt;index&gt;" static with the exports object as its LAST
    /// parameter; run is function 0 (no imported functions). A library that
    /// no longer names it that way gets the wrapper, and the deep
    /// backtracking test says whether that still holds up.</summary>
    private static Func<int, int, int> TailEntry(WasmRunExports exports)
    {
        var type = exports.GetType();
        var run = type.GetMethod("👻 0",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (run is null) return exports.run;
        var stub = new System.Reflection.Emit.DynamicMethod("run_tail", typeof(int),
            [typeof(WasmRunExports), typeof(int), typeof(int)], type, skipVisibility: true);
        var il = stub.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Castclass, type);
        il.Emit(System.Reflection.Emit.OpCodes.Tailcall);
        il.Emit(System.Reflection.Emit.OpCodes.Call, run);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        return (Func<int, int, int>)stub.CreateDelegate(typeof(Func<int, int, int>), exports);
    }

    public IReadOnlyList<int> Evict(IEnumerable<int> functorIds) => Modules.Evict(functorIds);

    public bool Contains(int functorId) => Modules.Contains(functorId);

    public bool TryResolve(int functorId, int address, out WasmTarget target)
        => Modules.TryResolve(functorId, address, out target);

    public int EntryAddressOf(int functorId) => Modules.EntryAddressOf(functorId);

    public IWasmChainContext BeginChain(Activation engine)
    {
        if (Modules.ModuleCount == 0)
            throw new InvalidOperationException("no module installed");
        return new Chain(this, engine);
    }

    public void Dispose() { if (_ownsSpace) _space.Dispose(); }

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
        private readonly Activation _engine;
        private readonly long[] _mailbox = new long[WasmAbi.SlotCount];
        private int _heapAt, _stackAt, _trailAt, _functorAt, _resumeAt, _moduleIndexAt;
        private int _attrAt, _callMarkerAt, _metaCacheAt, _atomMarkerAt;
        // Exactly one side is authoritative: the image (false) or the engine
        // (true, after SyncEngine ran and managed code may have mutated).
        private bool _engineAuthoritative;

        public Chain(DesktopWasmWorld w, Activation engine)
        {
            _w = w;
            _engine = engine;
            StageFromEngine();
        }

        /// <summary>Copies the engine's live heap, stack, registers and
        /// trail into linear memory, once per chain.
        ///
        /// <para>This is why a TIME taken on this world is not a measurement
        /// of the tier: the browser pins the engine's arrays and copies
        /// nothing, so a crossing costs O(1) there and O(live data) here.
        /// On clpr, whose heap grows as it runs, that made the per-crossing
        /// cost grow with the problem (35, 52, 91 us as the work doubled
        /// twice) and the tier look 5x slower than Tier-0 — where the same
        /// program in a browser is 1.1-2.2x faster. Use this world for
        /// correctness and for COUNTS, which are identical in both; measure
        /// time in a headless browser (CONTRIBUTING.md).</para></summary>
        private unsafe void StageFromEngine()
        {
            _engine.EnsureWasmRegisters(_w.Modules.RegisterDemand);
            // Turn the image on before reading its rows: an engine that never
            // meets a wasm world keeps paying nothing for it.
            _engine.AttrMirrorEnable();
            _engine.MetaResolutionObserver = _w.ResumeTable.NoteMetaResolution;
            Cell[] heap = _engine.WasmHeapView;
            Cell[] stack = _engine.WasmStackView;
            Cell[] regs = _engine.WasmRegistersView;
            int[] trail = _engine.WasmBindingTrailView;

            _heapAt = RegistersAt + regs.Length * 8;
            _stackAt = _heapAt + heap.Length * 8;
            _trailAt = _stackAt + stack.Length * 8;
            _functorAt = _trailAt + trail.Length * 4;
            int fcount = FunctorTable.IdLimit;
            long[] resumeRows = _w.ResumeTable.Rows;
            _resumeAt = _functorAt + fcount * 8;
            _moduleIndexAt = _resumeAt + resumeRows.Length * 8;
            int moduleCount = _w.ResumeTable.ModuleCount;
            long[] attrRows = _engine.AttrMirrorRows;
            int[] callMarkers = _w.ResumeTable.CallMarkers;
            long[] metaCache = _w.ResumeTable.MetaCache;
            int[] atomMarkers = _w.ResumeTable.AtomCallMarkers;
            // Rounded up to 8 for SPEED, not correctness: a wasm i64.load
            // may be unaligned (the align immediate is a hint), so no test
            // can fail on dropping this -- do not go looking for one.
            _attrAt = (_moduleIndexAt + moduleCount * 4 + 7) & ~7;
            _callMarkerAt = _attrAt + attrRows.Length * 8;
            _metaCacheAt = (_callMarkerAt + callMarkers.Length * 4 + 7) & ~7;
            _atomMarkerAt = _metaCacheAt + metaCache.Length * 8;
            if (_atomMarkerAt + atomMarkers.Length * 4 > (long)Pages * 65536)
                throw new InvalidOperationException("engine areas outgrew the desktop image");
            if (_functorAt != _w._space.FunctorAt)
            { _w._space.FunctorAt = _functorAt; _w._space.FunctorSynced = 0; }

            var bases = new Activation.WasmMailboxBases(
                _heapAt, _stackAt, RegistersAt, _trailAt,
                HeapLimitCells: heap.Length - 8,
                StackLimitCells: stack.Length - 8,
                TrailLimitEntries: trail.Length - 8,
                FunctorTableBase: _functorAt,
                ResumeTableBase: _resumeAt,
                ResumeTableRows: resumeRows.Length,
                ModuleIndexBase: _moduleIndexAt,
                AttrTableBase: attrRows.Length > 0 ? _attrAt : 0,
                AttrTableMask: _engine.AttrMirrorMask,
                CallMarkerBase: _callMarkerAt,
                CallMarkerLength: callMarkers.Length,
                MetaCacheBase: _metaCacheAt,
                MetaCacheMask: _w.ResumeTable.MetaCacheMask,
                AtomMarkerBase: _atomMarkerAt,
                AtomMarkerLength: atomMarkers.Length);
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
            if (_w._space.FunctorSynced < fcount)
            {
                int done = _w._space.FunctorSynced;
                var dest = new Span<long>(mem + _functorAt + done * 8L, fcount - done);
                _w._space.FunctorSynced = FunctorTable.CopyPackedFrom(done, dest);
            }
            fixed (long* p = resumeRows)
                Buffer.MemoryCopy(p, mem + _resumeAt, resumeRows.Length * 8L,
                                  resumeRows.Length * 8L);
            // moduleId -> function-table index. Here the two happen to be the
            // same number, because the world registers itself at its own id;
            // in the browser addFunction picks the index, so the indirection
            // is what makes one emitted form work in both.
            for (int i = 0; i < moduleCount; i++)
                *(int*)(mem + _moduleIndexAt + i * 4) = i;
            // Copied whole rather than incrementally: unlike the functor
            // table, which only ever grows, this one has rows removed and
            // re-keyed under it, so there is no "synced up to here" mark to
            // resume from. It is copied again at every restaging, which is
            // what makes a put_attr inside a builtin visible on re-entry.
            fixed (long* p = attrRows)
                Buffer.MemoryCopy(p, mem + _attrAt, attrRows.Length * 8L,
                                  attrRows.Length * 8L);
            // Copied only when it actually changed: install and eviction are
            // the only writers, so a run that promotes nothing copies this
            // once. Without the check queens re-copies 8 KB on each of its
            // 38,000 chains, which is the copy world inventing a cost the
            // browser does not have.
            if (_callMarkerAt != _w._space.CallMarkerAt
                || _w.ResumeTable.CallMarkerVersion != _w._space.CallMarkerCopied)
            {
                fixed (int* p = callMarkers)
                    Buffer.MemoryCopy(p, mem + _callMarkerAt, callMarkers.Length * 4L,
                                      callMarkers.Length * 4L);
                fixed (int* p = atomMarkers)
                    Buffer.MemoryCopy(p, mem + _atomMarkerAt, atomMarkers.Length * 4L,
                                      atomMarkers.Length * 4L);
                _w._space.CallMarkerAt = _callMarkerAt;
                _w._space.CallMarkerCopied = _w.ResumeTable.CallMarkerVersion;
            }
            // The meta cache settles once the working set is in, so this
            // copies a handful of times and then never again.
            if (_metaCacheAt != _w._space.MetaCacheAt
                || _w.ResumeTable.MetaCacheVersion != _w._space.MetaCacheCopied)
            {
                fixed (long* p = metaCache)
                    Buffer.MemoryCopy(p, mem + _metaCacheAt, metaCache.Length * 8L,
                                      metaCache.Length * 8L);
                _w._space.MetaCacheAt = _metaCacheAt;
                _w._space.MetaCacheCopied = _w.ResumeTable.MetaCacheVersion;
            }
            _engineAuthoritative = false;
        }

        public WasmVerdict Call(WasmTarget target)
            => (WasmVerdict)_w._space.Instances[target.ModuleId].Exports
                .run(MailboxAt, target.Cursor);

        public bool TryResolve(int functorId, int address, out WasmTarget target)
            => _w.Modules.TryResolve(functorId, address, out target);

        private WasmModuleRegistry.Module Current
            => _w.Modules.ById((int)ReadSlot(WasmAbi.CurrentModuleId));

        public long TranslatePcToLive(long buildPc)
            => Current.AddrIndex.Translate(buildPc, _w._liveByFid);

        public int OwnerFunctorOf(long buildPc) => Current.AddrIndex.OwnerFunctorOf(buildPc);

        public long ReadSlot(int slot)
            => Marshal.ReadInt64(_w._memory.Start, MailboxAt + slot * WasmAbi.SlotSize);

        // An address in this world is an offset into the private image.
        public long ReadWord(long address)
            => Marshal.ReadInt64(_w._memory.Start, (int)address);

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
