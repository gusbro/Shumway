using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;

namespace Shumway.Web;

/// <summary>The browser's wasm execution world: the engine's modules, each
/// pinned and registered lazily per thread (with threads on, every worker
/// has its own function table; only the memory is shared), resolving through
/// one resume table whose rows are pinned in place. A chain pins the engine
/// arrays once, fills the pinned mailbox once, and every call is a raw hop
/// through this thread's table; a marker of a sibling module is a tail call
/// inside wasm. All C# here runs MONO-INTERPRETED, which is why per-entry
/// work is hoisted into the chain open/close.</summary>
internal sealed class BrowserWasmWorld : IWasmExecutionWorld
{
    private readonly WasmResumeTable _table = new();
    private readonly WasmModuleRegistry _modules;

    // Timing split for the probe: ticks inside raw calls vs staging
    // (BeginChain + RefreshFromEngine). Process-wide diagnostics.
    internal static long DiagCallTicks, DiagStageTicks;

    /// <summary>Wall time inside the per-thread module registration
    /// (compile + instantiate + addFunction). Diagnostic.</summary>
    internal static long DiagRegisterTicks;

    /// <summary>One registered module: pinned patched bytes and the
    /// per-thread table index. Never removed (a chain may be inside), and
    /// registered functions are never unregistered.</summary>
    private sealed record Registration(byte[] PinnedModule, ThreadLocal<int> Index);

    // Indexed by module id.
    private readonly List<Registration> _registrations = new();

    // The rows the modules read, pinned where the table keeps them; repinned
    // when the table grows (an install, never mid-call).
    private long[]? _pinnedRows;
    private GCHandle _rowsPin;

    // moduleId -> this thread's table index, -1 where not registered here.
    // Filled at each staging: registering is per thread and costs a compile,
    // so it happens the first time a thread stages a chain over the module,
    // not at install.
    private readonly ThreadLocal<int[]> _moduleIndex = new(() =>
    {
        var a = GC.AllocateArray<int>(16, pinned: true);
        Array.Fill(a, -1);
        return a;
    });

    public BrowserWasmWorld() => _modules = new WasmModuleRegistry(_table);

    // (fid -> live linked address) after a relink; null until one happens.
    // Reference-swapped at a boundary tick, read lock-free by chains.
    private volatile IReadOnlyDictionary<int, int>? _liveByFid;

    public void RefreshLiveAddresses(IReadOnlyDictionary<int, int> liveByFid)
        => _liveByFid = liveByFid;

    public int LiveEntryAddressOf(int functorId)
        => _liveByFid is { } live && live.TryGetValue(functorId, out int at)
            ? at : EntryAddressOf(functorId);

    public long TranslatePcToLive(int functorId, long buildPc)
        => _modules.TranslatePcToLive(functorId, buildPc, _liveByFid);

    /// <summary>The id the next install will get: what a module has to be
    /// compiled against, since the id is baked into its probes.</summary>
    public int NextModuleId => _table.ModuleCount;

    public int ModuleCount => _table.ModuleCount;

    /// <summary>Rows in the resume table (markers it can answer for).</summary>
    public int ResumeRows => _table.Length;

    /// <summary>Wasm bytes handed to the browser, all modules.</summary>
    public long ModuleBytes { get; private set; }

    public IReadOnlyList<int> InstallGroup(byte[] module,
        IReadOnlyDictionary<int, int> entryCursorByFid,
        IReadOnlyDictionary<int, int> cursorByAddress,
        IReadOnlyDictionary<int, int> entryAddressByFid,
        int registerDemand,
        IEnumerable<(int Caller, int Callee)> callEdges)
    {
        byte[] patched = WasmSharedMemory.Patch(module);
        byte[] pinned = GC.AllocateArray<byte>(patched.Length, pinned: true);
        patched.CopyTo(pinned, 0);
        var index = new ThreadLocal<int>(() =>
        {
            int at = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(pinned, 0);
            long t0 = Stopwatch.GetTimestamp();
            int i = WebShumwayApp.WasmRegister(at, pinned.Length);
            DiagRegisterTicks += Stopwatch.GetTimestamp() - t0;
            if (i < 0)
                throw new WasmRegisterException(
                    $"the module ({pinned.Length} bytes) did not register"
                    + " — see the browser console for the engine's reason");
            return i;
        });
        // EAGERLY on the installing thread: a module the browser refuses (a
        // V8 size limit, say — a giant jit_compile(all) group) must fail
        // HERE, where the caller can fall back to bytecode and report,
        // never inside some later user query's first chain call. Every pool
        // thread is the same kind of worker, so this thread's verdict
        // stands for the others. Registering before minting the id keeps a
        // refused module out of the registry altogether.
        _ = index.Value;
        var m = _modules.Install(entryCursorByFid, cursorByAddress, entryAddressByFid,
                                 registerDemand, callEdges, out var displaced);
        if (m.Id != _registrations.Count)
            throw new InvalidOperationException("module id out of step with registrations");
        _registrations.Add(new Registration(pinned, index));
        ModuleBytes += pinned.Length;
        return displaced;
    }

    public IReadOnlyList<int> Evict(IEnumerable<int> functorIds) => _modules.Evict(functorIds);

    public bool Contains(int functorId) => _modules.Contains(functorId);

    public bool TryResolve(int functorId, int address, out WasmTarget target)
        => _modules.TryResolve(functorId, address, out target);

    public int EntryAddressOf(int functorId) => _modules.EntryAddressOf(functorId);

    public IWasmChainContext BeginChain(Activation engine)
    {
        if (_table.ModuleCount == 0)
            throw new InvalidOperationException("no module installed");
        return new Chain(this, engine);
    }

    /// <summary>The rows' address, repinning if the table grew.</summary>
    private long RowsAddress()
    {
        long[] rows = _table.Rows;
        if (!ReferenceEquals(_pinnedRows, rows))
        {
            if (_rowsPin.IsAllocated) _rowsPin.Free();
            _rowsPin = GCHandle.Alloc(rows, GCHandleType.Pinned);
            _pinnedRows = rows;
        }
        return (long)_rowsPin.AddrOfPinnedObject();
    }

    /// <summary>This thread's module index, brought up to date with every
    /// installed module (registering the ones this thread has not seen).</summary>
    private long ModuleIndexAddress()
    {
        int[] a = _moduleIndex.Value!;
        int count = _registrations.Count;
        if (a.Length < count)
        {
            int grown = a.Length;
            while (grown < count) grown *= 2;
            var next = GC.AllocateArray<int>(grown, pinned: true);
            Array.Fill(next, -1);
            Array.Copy(a, next, a.Length);
            _moduleIndex.Value = a = next;
        }
        for (int i = 0; i < count; i++)
            if (a[i] < 0) a[i] = _registrations[i].Index.Value;
        return (long)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(a, 0);
    }

    private sealed class Chain : IWasmChainContext
    {
        private readonly BrowserWasmWorld _w;
        private readonly Activation _engine;
        private readonly long[] _mailbox = GC.AllocateArray<long>(WasmAbi.SlotCount, pinned: true);
        private readonly int _mailboxAt;
        private GCHandle _heapPin, _stackPin, _regsPin, _trailPin, _attrPin;
        private long[]? _attrRows;
        private GCHandle _callPin;
        private int[]? _callMarkers;
        private GCHandle _metaPin;
        private long[]? _metaCache;
        private GCHandle _atomPin;
        private int[]? _atomMarkers;
        private Cell[] _heap = null!, _stack = null!, _regs = null!;
        private int[] _trail = null!;
        private bool _engineAuthoritative;

        public Chain(BrowserWasmWorld w, Activation engine)
        {
            _w = w;
            _engine = engine;
            _mailboxAt = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(_mailbox, 0);
            Stage();
        }

        /// <summary>Pins the engine areas and fills the mailbox with their
        /// real addresses plus the scalars. The arrays are only replaced
        /// (growth, GC) by managed code, and managed code only runs with the
        /// chain synced back to the engine, so the pins are stable for the
        /// life of the staging -- D2. The table rows and the module index
        /// are re-read here too: an install during a nested builtin becomes
        /// visible at the restaging that follows it.</summary>
        private void Stage()
        {
            long t0 = Stopwatch.GetTimestamp();
            _engine.EnsureWasmRegisters(_w._modules.RegisterDemand);
            _engine.AttrMirrorEnable();
            _engine.MetaResolutionObserver = _w._modules.NoteMetaResolution;
            var heap = _engine.WasmHeapView;
            var stack = _engine.WasmStackView;
            var regs = _engine.WasmRegistersView;
            var trail = _engine.WasmBindingTrailView;
            RepinIfChanged(ref _heapPin, ref _heap, heap);
            RepinIfChanged(ref _stackPin, ref _stack, stack);
            RepinIfChanged(ref _regsPin, ref _regs, regs);
            if (!ReferenceEquals(_trail, trail))
            {
                if (_trailPin.IsAllocated) _trailPin.Free();
                _trailPin = GCHandle.Alloc(trail, GCHandleType.Pinned);
                _trail = trail;
            }
            var bases = new Activation.WasmMailboxBases(
                (long)_heapPin.AddrOfPinnedObject(),
                (long)_stackPin.AddrOfPinnedObject(),
                (long)_regsPin.AddrOfPinnedObject(),
                (long)_trailPin.AddrOfPinnedObject(),
                HeapLimitCells: heap.Length - 8,
                StackLimitCells: stack.Length - 8,
                TrailLimitEntries: trail.Length - 8,
                FunctorTableBase: BrowserWasmTier.FunctorMirrorAddress(),
                ResumeTableBase: _w.RowsAddress(),
                ResumeTableRows: _w._table.Length,
                ModuleIndexBase: _w.ModuleIndexAddress(),
                TraceBase: TraceRingAddress(),
                TraceLimit: _traceRing?.Length ?? 0,
                AttrTableBase: AttrMirrorAddress(),
                AttrTableMask: _engine.AttrMirrorMask,
                FdDomFunctorBase: FdDomFunctorAddress(),
                FdDomFunctorLength: FdDomFunctorTable.Cells.Length,
                CallMarkerBase: CallMarkerAddress(),
                CallMarkerLength: _w._table.CallMarkers.Length,
                MetaCacheBase: MetaCacheAddress(),
                MetaCacheMask: _w._table.MetaCacheMask,
                AtomMarkerBase: AtomMarkerAddress(),
                AtomMarkerLength: _w._table.AtomCallMarkers.Length,
                AttrLogBase: AttrLogAddress(),
                AttrLogLength: _engine.AttrLogCount,
                ExtraTrailBase: ExtraTrailAddress(),
                AttrOrphanBase: OrphanRingAddress(),
                AttrOrphanLimit: _engine.WasmOrphanRingView.Length,
                ArithTableBase: ArithTableAddress(),
                ArithTableLength: Shumway.Builtins.ArithFunctorTable.Length,
                AttrWriteBase: AttrWriteRingAddress(),
                AttrWriteLimit: _engine.WasmAttrWriteRingView.Length / 4,
                ExtraTrailLimitEntries: _engine.WasmExtraTrailView.Length - 8,
                AttrMirrorBudget: _engine.AttrMirrorInsertBudget);
            if (!_engine.TryFillWasmMailbox(_mailbox, bases))
                throw new InvalidOperationException(
                    "a mode-incompatible activation reached the wasm world");
            _engineAuthoritative = false;
            DiagStageTicks += Stopwatch.GetTimestamp() - t0;
        }

        /// <summary>The '$fd_dom' functor table's address (ADR-051). Pinned
        /// ONCE: the contents are interned at startup and never change, so
        /// unlike every other area here there is nothing to repin.</summary>
        private static GCHandle _fdDomPin;

        /// <summary>TRACE MODE's ring, pinned for the module to write into.
        /// Allocated only once something arms the trace, so an ordinary run
        /// carries neither the buffer nor the pin.</summary>
        private static long[]? _traceRing;
        private static GCHandle _tracePin;

        private static long TraceRingAddress()
        {
            if (!Shumway.Core.Diagnostics.CommitTrace.Enabled) return 0;
            if (_traceRing is null)
            {
                _traceRing = new long[64 * 1024];
                _tracePin = GCHandle.Alloc(_traceRing, GCHandleType.Pinned);
            }
            return (long)_tracePin.AddrOfPinnedObject();
        }

        /// <summary>Moves whatever the module wrote into the host's trace and
        /// empties the ring. Called wherever the chain comes out, so the two
        /// tiers' events land in ONE stream in the order they happened.
        /// </summary>
        private void DrainTraceRing()
        {
            if (_traceRing is null) return;
            int n = (int)ReadSlot(WasmAbi.TraceTop);
            for (int i = 0; i < n && i < _traceRing.Length; i++)
            {
                long rec = _traceRing[i];
                int kind = (int)(rec & 0xFF);
                int payload = (int)(rec >> 8);
                Shumway.Core.Diagnostics.CommitTrace.Note(
                    _engine.CellsAllocated,
                    kind == 1 ? Shumway.Core.Diagnostics.CommitTrace.Kind.Push
                              : Shumway.Core.Diagnostics.CommitTrace.Kind.Cut,
                    -1, payload);
            }
            WriteSlot(WasmAbi.TraceTop, 0);
        }

        private static long FdDomFunctorAddress()
        {
            if (!_fdDomPin.IsAllocated)
                _fdDomPin = GCHandle.Alloc(FdDomFunctorTable.Cells,
                                           GCHandleType.Pinned);
            return (long)_fdDomPin.AddrOfPinnedObject();
        }

        /// <summary>The atom marker table's address, repinning when the host
        /// grew it -- same rule as the call markers.</summary>
        private long AtomMarkerAddress()
        {
            int[] markers = _w._table.AtomCallMarkers;
            if (!ReferenceEquals(_atomMarkers, markers))
            {
                if (_atomPin.IsAllocated) _atomPin.Free();
                _atomPin = GCHandle.Alloc(markers, GCHandleType.Pinned);
                _atomMarkers = markers;
            }
            return (long)_atomPin.AddrOfPinnedObject();
        }

        /// <summary>The meta cache's address. It is never replaced once
        /// allocated, so this pins once.</summary>
        private long MetaCacheAddress()
        {
            long[] rows = _w._table.MetaCache;
            if (!ReferenceEquals(_metaCache, rows))
            {
                if (_metaPin.IsAllocated) _metaPin.Free();
                _metaPin = GCHandle.Alloc(rows, GCHandleType.Pinned);
                _metaCache = rows;
            }
            return (long)_metaPin.AddrOfPinnedObject();
        }

        /// <summary>The call-marker table's address, repinning when the host
        /// grew it. Same rule as the rows: the array is replaced on growth, so
        /// the address cannot be cached across a staging.</summary>
        private long CallMarkerAddress()
        {
            int[] markers = _w._table.CallMarkers;
            if (!ReferenceEquals(_callMarkers, markers))
            {
                if (_callPin.IsAllocated) _callPin.Free();
                _callPin = GCHandle.Alloc(markers, GCHandleType.Pinned);
                _callMarkers = markers;
            }
            return (long)_callPin.AddrOfPinnedObject();
        }

        /// <summary>The attribute trail log's home column, pinned the same
        /// way and for the same reason: the array is replaced on growth, so
        /// caching the address across a staging would hand the module a
        /// freed one. Enabling it here is what makes an engine that never
        /// meets a wasm world pay nothing for attribute mutation.</summary>
        private GCHandle _attrLogPin;
        private int[]? _attrLogHomesPinned;

        /// <summary>The extra trail, pinned. The one area the module
        /// REWRITES rather than only appending to -- a cut compacts it in
        /// place -- which in this world costs nothing extra, because pinning
        /// means the engine's array IS the image and there is no copy back
        /// to get wrong.</summary>
        private GCHandle _extraTrailPin;
        private ExtraTrailEntry[]? _extraTrailPinned;

        private long ExtraTrailAddress()
        {
            ExtraTrailEntry[] t = _engine.WasmExtraTrailView;
            if (t.Length == 0) return 0;
            if (!ReferenceEquals(_extraTrailPinned, t))
            {
                if (_extraTrailPin.IsAllocated) _extraTrailPin.Free();
                _extraTrailPin = GCHandle.Alloc(t, GCHandleType.Pinned);
                _extraTrailPinned = t;
            }
            return (long)_extraTrailPin.AddrOfPinnedObject();
        }

        /// <summary>The orphaned-record ring, pinned. The module writes,
        /// the host drains on the way out.</summary>
        private GCHandle _orphanPin;
        private int[]? _orphanPinned;

        /// <summary>The arithmetic functor table, pinned. It is replaced
        /// when it grows with the functor table, so the address is re-taken
        /// rather than cached across a staging.</summary>
        private static GCHandle _arithPin;
        private static int[]? _arithPinned;

        private static long ArithTableAddress()
        {
            int[] r = Shumway.Builtins.ArithFunctorTable.Rows;
            if (r.Length == 0) return 0;
            if (!ReferenceEquals(_arithPinned, r))
            {
                if (_arithPin.IsAllocated) _arithPin.Free();
                _arithPin = GCHandle.Alloc(r, GCHandleType.Pinned);
                _arithPinned = r;
            }
            return (long)_arithPin.AddrOfPinnedObject();
        }

        /// <summary>The parked attribute writes, pinned.</summary>
        private GCHandle _attrWritePin;
        private int[]? _attrWritePinned;

        private long AttrWriteRingAddress()
        {
            int[] r = _engine.WasmAttrWriteRingView;
            if (!ReferenceEquals(_attrWritePinned, r))
            {
                if (_attrWritePin.IsAllocated) _attrWritePin.Free();
                _attrWritePin = GCHandle.Alloc(r, GCHandleType.Pinned);
                _attrWritePinned = r;
            }
            return (long)_attrWritePin.AddrOfPinnedObject();
        }

        private long OrphanRingAddress()
        {
            int[] r = _engine.WasmOrphanRingView;
            if (!ReferenceEquals(_orphanPinned, r))
            {
                if (_orphanPin.IsAllocated) _orphanPin.Free();
                _orphanPin = GCHandle.Alloc(r, GCHandleType.Pinned);
                _orphanPinned = r;
            }
            return (long)_orphanPin.AddrOfPinnedObject();
        }

        private long AttrLogAddress()
        {
            _engine.AttrLogMirrorEnable();
            if (_engine.AttrLogCount == 0) return 0;
            int[] homes = _engine.AttrLogHomes;
            if (!ReferenceEquals(_attrLogHomesPinned, homes))
            {
                if (_attrLogPin.IsAllocated) _attrLogPin.Free();
                _attrLogPin = GCHandle.Alloc(homes, GCHandleType.Pinned);
                _attrLogHomesPinned = homes;
            }
            return (long)_attrLogPin.AddrOfPinnedObject();
        }

        /// <summary>The attribute image's address, repinning when the host
        /// replaced the array. It is replaced on every growth and on every
        /// rebuild, so caching the address across a staging would hand the
        /// module a freed one.</summary>
        private long AttrMirrorAddress()
        {
            long[] rows = _engine.AttrMirrorRows;
            if (rows.Length == 0) return 0;
            if (!ReferenceEquals(_attrRows, rows))
            {
                if (_attrPin.IsAllocated) _attrPin.Free();
                _attrPin = GCHandle.Alloc(rows, GCHandleType.Pinned);
                _attrRows = rows;
            }
            return (long)_attrPin.AddrOfPinnedObject();
        }

        private static void RepinIfChanged(ref GCHandle pin, ref Cell[] cached, Cell[] current)
        {
            if (ReferenceEquals(cached, current)) return;
            if (pin.IsAllocated) pin.Free();
            pin = GCHandle.Alloc(current, GCHandleType.Pinned);
            cached = current;
        }

        public WasmVerdict Call(WasmTarget target)
        {
            long t0 = Stopwatch.GetTimestamp();
            int v = WebShumwayApp.WasmCall(_w._registrations[target.ModuleId].Index.Value,
                                           _mailboxAt, target.Cursor);
            DiagCallTicks += Stopwatch.GetTimestamp() - t0;
            return (WasmVerdict)v;
        }

        public bool TryResolve(int functorId, int address, out WasmTarget target)
            => _w._modules.TryResolve(functorId, address, out target);

        private WasmModuleRegistry.Module Current
            => _w._modules.ById((int)_mailbox[WasmAbi.CurrentModuleId]);

        public long TranslatePcToLive(long buildPc)
            => Current.AddrIndex.Translate(buildPc, _w._liveByFid);

        public int OwnerFunctorOf(long buildPc) => Current.AddrIndex.OwnerFunctorOf(buildPc);

        public long ReadSlot(int slot) => _mailbox[slot];

        public void WriteSlot(int slot, long value) => _mailbox[slot] = value;

        // Here an address IS a runtime address: the module's memory and the
        // host's are the same one, which is the whole reason this world pins
        // instead of copying.
        public long ReadWord(long address) => Marshal.ReadInt64((nint)address);

        public void SyncEngine()
        {
            if (_engineAuthoritative) return;
            // Whatever the module traced belongs in the stream BEFORE the
            // host resumes writing to it, or the two tiers interleave wrong.
            DrainTraceRing();
            _engine.SyncFromWasmMailbox(_mailbox);
            _engineAuthoritative = true;
        }

        public void RefreshFromEngine()
        {
            if (!_engineAuthoritative)
                throw new InvalidOperationException("refresh without a preceding sync");
            Stage();
        }

        public void Dispose()
        {
            SyncEngine();
            if (_heapPin.IsAllocated) _heapPin.Free();
            if (_stackPin.IsAllocated) _stackPin.Free();
            if (_regsPin.IsAllocated) _regsPin.Free();
            if (_trailPin.IsAllocated) _trailPin.Free();
        }
    }
}

/// <summary>Boot-time wiring of the wasm tier: the promotion store's
/// <c>Promoter</c> compiles each promoted predicate as a module of its own
/// (the batch path, all candidates as one), installs it into the engine's
/// world, and wraps the chain-driving verdict loop. Gated by
/// <see cref="RuntimeCaps.SupportsWasmCodegen"/> -- only Shumway.Web turns
/// the feature switch on.</summary>
internal static class BrowserWasmTier
{
    // The functor-arity mirror the general unifier reads, pinned and
    // process-wide: append-only under the lock, read lock-free by the wasm.
    private static long[] _functorMirror = GC.AllocateArray<long>(4096, pinned: true);
    private static int _functorSynced;
    private static readonly object _functorMirrorLock = new();


    /// <summary>The deopt sites that actually cost, heaviest first, each
    /// named by the instruction it steps aside at. A deopt pays a full image
    /// staging, so a handful of hot sites can dominate a run: this is the
    /// list that says which one to open-code next, instead of guessing from
    /// the total.</summary>
    /// <summary>The functors that asked for the last group build, named.</summary>
    internal static string TriggerNames(WasmPromotionStore w)
    {
        var names = new List<string>();
        foreach (int f in w.LastBatchTrigger)
        {
            var (aid, ar) = FunctorTable.Lookup(f);
            names.Add($"{AtomTable.GetById(aid)?.Name}/{ar}");
            if (names.Count == 6) break;
        }
        return string.Join(" ", names)
            + (w.LastBatchTrigger.Count > 6
                ? $" and {w.LastBatchTrigger.Count - 6} more" : "");
    }

    /// <summary>The builtin exits, ranked. Same shape as the deopt ranking
    /// and for the same reason: a total says a storm happened, a ranking
    /// says which one.</summary>
    internal static string BuiltinRankingReport()
    {
        if (!WasmTierDelegate.DiagCompiledIn) return "";
        var rank = WasmTierDelegate.BuiltinRanking();
        if (rank.Count == 0) return "";
        long total = WasmTierDelegate.DiagBuiltins;
        var sb = new System.Text.StringBuilder();
        sb.Append("%   builtin exits (of ").Append(total).Append(", ")
          .Append(rank.Count).Append(" distinct):\n");
        for (int i = 0; i < rank.Count && i < 24; i++)
        {
            var (name, arity, hits) = rank[i];
            double pct = total > 0 ? hits * 100.0 / total : 0;
            sb.Append($"%     {hits} ({pct:F0}%) {name}/{arity}\n");
        }
        return sb.ToString();
    }

    /// <summary>Which buffer last ran out, if one did. The ISO term says
    /// only "memory", which is right for a program and useless for finding
    /// out why one engine ran out where another did not.</summary>
    /// <summary>Who ASKS for the builtins, heaviest pair first. The plain
    /// tally says which builtin a run leaves for; when one of them is the
    /// whole run, the next question is always who is asking, and the answer
    /// names a clause to read.</summary>
    internal static string BuiltinCallerReport()
    {
        var rank = WasmTierDelegate.BuiltinCallerRanking();
        if (rank.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append("%   builtin callers (").Append(rank.Count)
          .Append(" pairs):").Append(System.Environment.NewLine);
        for (int i = 0; i < rank.Count && i < 10; i++)
        {
            var (bid, fid, hits) = rank[i];
            string bn;
            try
            {
                var e2 = Shumway.Builtins.BuiltinsRegistry.GetById(bid);
                bn = e2.Name + "/" + e2.Arity;
            }
            catch (System.Exception) { bn = "?id" + bid; }
            string cn;
            try
            {
                var (aid, ar) = Shumway.Core.FunctorTable.Lookup(fid);
                cn = (Shumway.Core.AtomTable.GetById(aid)?.Name ?? "?") + "/" + ar;
            }
            catch (System.Exception) { cn = "fid" + fid; }
            sb.Append("%     ").Append(hits).Append(' ').Append(bn)
              .Append(" from ").Append(cn).Append(System.Environment.NewLine);
        }
        return sb.ToString();
    }

    internal static string AreaReport()
        => "%   high water: stackTop=" + WasmTierDelegate.DiagMaxStackTop
         + " choiceTop=" + WasmTierDelegate.DiagMaxChoiceTop
         + System.Environment.NewLine
         + (WasmTierDelegate.DiagCpCensus is { } c
             ? "%   cp chain: " + c + System.Environment.NewLine : "")
         + (WasmTierDelegate.DiagEnvCensus is { } e
             ? "%   env chain: " + e + System.Environment.NewLine : "");

    /// <summary>Where the delegate's time went. The pieces sum:
    /// delegate = inside wasm + staging + builtins + the glue of the
    /// verdict loop, and a run that is slower than Tier 0 has to say
    /// WHICH of those it is before anything is tuned.</summary>
    internal static string TimingReport()
    {
        double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double del = WasmTierDelegate.DiagDelegateTicks * f;
        double inw = BrowserWasmWorld.DiagCallTicks * f;
        double stg = BrowserWasmWorld.DiagStageTicks * f;
        double bti = WasmTierDelegate.DiagBuiltinTicks * f;
        return $"%   delegate {del:F1} ms = inWasm {inw:F1} + stage {stg:F1}"
             + $" + builtins {bti:F1} + glue {del - inw - stg - bti:F1}"
             + System.Environment.NewLine;
    }

    internal static string CompactReport()
        => "%   cut compactions: " + Shumway.Core.Diagnostics.CompactCensus.Walks
         + " walks, " + Shumway.Core.Diagnostics.CompactCensus.Dropped
         + " dropped something; "
         + Shumway.Core.Diagnostics.CompactCensus.ReadTheLog + " read the log, "
         + Shumway.Core.Diagnostics.CompactCensus.WroteTheLog + " wrote it, "
         + Shumway.Core.Diagnostics.CompactCensus.DroppedARecord + " dropped a record, "
         + Shumway.Core.Diagnostics.CompactCensus.ClippedAFrame + " clipped a frame; "
         + Shumway.Core.Diagnostics.CompactCensus.ReachableByAnImage
         + " need only a READ image; "
         + Shumway.Core.Diagnostics.CompactCensus.OrphansCleared
         + " records parked by the module and cleared here; put_atts "
         + Shumway.Core.Diagnostics.CompactCensus.AttrPutUpdate + " update / "
         + Shumway.Core.Diagnostics.CompactCensus.AttrPutInsert + " insert"
         + System.Environment.NewLine;

    internal static string ExhaustionReport()
        => Shumway.Core.Activation.LastExhausted is { } b
            ? "%   last resource_error(memory): " + b + "\n" : "";

    /// <summary>The callees a chain could not continue into, heaviest
    /// first. A foreign exit means the callee has no module at all, so
    /// the chain closes and the interpreter runs it -- the count says how
    /// much there is to remove, and this says what.</summary>
    internal static string ForeignRankingReport(PrologEngine engine)
    {
        var rank = WasmTierDelegate.ForeignRanking();
        if (rank.Count == 0) return "";
        // Does the callee EXIST as a static predicate, and at what
        // address? A functor that names one which was simply not
        // promoted is a promotion question; one that names none, or one
        // sharing an address with a mangled sibling, is a naming bug.
        var addrOf = new Dictionary<int, int>();
        foreach (var (a2, p) in WasmPromotionStore.StaticPredicatesOf(engine))
            addrOf[p.FunctorId] = a2;
        var sb = new System.Text.StringBuilder();
        long total = WasmTierDelegate.DiagForeignExits;
        sb.Append("%   foreign callees (of ").Append(total).Append(", ")
          .Append(rank.Count).Append(" distinct):\n");
        for (int i = 0; i < rank.Count && i < 16; i++)
        {
            var (fid, hits) = rank[i];
            var (aid, ar) = Shumway.Core.FunctorTable.Lookup(fid);
            double pct = total > 0 ? hits * 100.0 / total : 0;
            string name = Shumway.Core.AtomTable.GetById(aid)?.Name ?? "?";
            string where = addrOf.TryGetValue(fid, out int at)
                ? $" static@{at}" : " NOT a static predicate";
            // The same name under some module, and where THAT sits.
            string twin = "";
            foreach (var kv in addrOf)
            {
                var (taid, tar) = Shumway.Core.FunctorTable.Lookup(kv.Key);
                string tn = Shumway.Core.AtomTable.GetById(taid)?.Name ?? "";
                if (tar != ar || kv.Key == fid) continue;
                if (tn == name || tn.EndsWith("$" + name, System.StringComparison.Ordinal))
                    twin += $" | {tn}/{tar}@{kv.Value}";
            }
            sb.Append($"%     {hits} ({pct:F0}%) {name}/{ar}{where}{twin}\n");
        }
        return sb.ToString();
    }

    internal static string DeoptRankingReport(PrologEngine engine)
    {
        var rank = WasmTierDelegate.DeoptRanking();
        if (rank.Count == 0) return "";
        var spans = new List<(int Lo, int Hi, Shumway.Compiler.Wam.CompiledPredicate Pred)>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
            spans.Add((addr, addr + pred.Bytecode.Length, pred));

        var sb = new System.Text.StringBuilder();
        long total = WasmTierDelegate.DiagDeopts;
        // The DISTINCT count leads: a flat spread over hundreds of sites and a
        // storm at three are different problems, and only this number tells
        // them apart. A truncated list looks the same either way.
        sb.Append("%   deopt sites (of ").Append(total).Append(", ")
          .Append(rank.Count).Append(" distinct):\n");
        for (int i = 0; i < rank.Count && i < 24; i++)
        {
            var (pc, hits) = rank[i];
            string where = $"0x{pc:X}";
            foreach (var sp in spans)
                if (pc >= sp.Lo && pc < sp.Hi)
                {
                    var (aid, ar) = Shumway.Core.FunctorTable.Lookup(sp.Pred.FunctorId);
                    var op = (Shumway.Core.Opcode)sp.Pred.Bytecode[pc - sp.Lo];
                    where = $"{Shumway.Core.AtomTable.GetById(aid)?.Name}/{ar}"
                          + $"@+{pc - sp.Lo} {op}";
                    break;
                }
            double pct = total > 0 ? hits * 100.0 / total : 0;
            sb.Append($"%     {hits} ({pct:F0}%) {where}\n");
        }
        if (WasmTierDelegate.DiagDeoptOverflow > 0)
            sb.Append($"%     {WasmTierDelegate.DiagDeoptOverflow} at sites past the table\n");

        // WHY a meta-call declined, not just where. A site named
        // "$wake_call/1@+28 CallBuiltin" says a meta-call went to the host
        // and nothing more: the goal could be a builtin no marker can name,
        // a pair the host never published, or a shape this path does not
        // handle, and those want three different fixes.
        var hist = WasmTierDelegate.DiagMetaGuardHist;
        long guards = 0;
        for (int g = 0; g < hist.Length; g++) guards += hist[g];
        if (guards > 0)
        {
            sb.Append("%   deopt reasons (of ").Append(guards).Append("):\n");
            for (int g = 0; g < hist.Length; g++)
            {
                if (hist[g] == 0) continue;
                sb.Append($"%     {hist[g]} guard {g}: ")
                  .Append(Shumway.Compiler.Wasm.WasmPredicateCompiler.DeoptReasonName(g));
                // The functor LAST seen at this code, where the site stamped
                // one: a row saying 400 meta-calls found no marker only
                // becomes actionable when it says 400 of WHAT.
                long gfid = WasmTierDelegate.DiagGuardFids[g];
                // Guard 34 stamps a cell TAG there, not a functor: what
                // an arithmetic operand turned out to be is the whole
                // question, and reading it as a functor id would name
                // some unrelated predicate.
                if (g == 34)
                {
                    var t34 = (Shumway.Core.Tag)(gfid >> 32);
                    sb.Append("  [last tag: ").Append(t34);
                    if (t34 == Shumway.Core.Tag.Str && (int)gfid != 0)
                    {
                        var (a34, r34) = Shumway.Core.FunctorTable.Lookup((int)gfid);
                        sb.Append(" = ").Append(Shumway.Core.AtomTable.GetById(a34)?.Name)
                          .Append('/').Append(r34);
                    }
                    sb.Append(']');
                }
                else if (gfid > 0)
                {
                    var (gaid, gar) = Shumway.Core.FunctorTable.Lookup((int)gfid);
                    sb.Append("  [last: ")
                      .Append(Shumway.Core.AtomTable.GetById(gaid)?.Name)
                      .Append('/').Append(gar).Append(']');
                }
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    internal static long FunctorMirrorAddress()
    {
        SyncFunctorMirror();
        return (long)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(_functorMirror, 0);
    }

    /// <summary>Keeps the linear-memory image of the functor table current:
    /// an exact copy of the packed (atomId, arity) array, extended in place
    /// from where the last sync stopped.</summary>
    private static void SyncFunctorMirror()
    {
        int count = FunctorTable.IdLimit;
        if (count <= Volatile.Read(ref _functorSynced)) return;
        lock (_functorMirrorLock)
        {
            if (count > _functorMirror.Length)
            {
                int grown = _functorMirror.Length;
                while (grown < count) grown *= 2;
                var next = GC.AllocateArray<long>(grown, pinned: true);
                Array.Copy(_functorMirror, next, _functorMirror.Length);
                _functorMirror = next;
                // The new address is picked up at the NEXT staging; the old
                // pinned array stays valid for any chain in flight.
            }
            int synced = _functorSynced;
            Volatile.Write(ref _functorSynced,
                FunctorTable.CopyPackedFrom(synced,
                    _functorMirror.AsSpan(synced, count - synced)));
        }
    }

    // The CURRENT engine's world; the stdlib bundle's module installs into it.
    private static BrowserWasmWorld? _world;

    internal static int ModuleCount() => _world?.ModuleCount ?? 0;


    internal static int ResumeRows() => _world?.ResumeRows ?? 0;
    internal static long ModuleBytes() => _world?.ModuleBytes ?? 0;

    /// <summary>Attaches the wasm promotion store to an engine. No-op when
    /// the capability is off.</summary>
    /// <summary>Set by jit_compile(off) and honoured by the BOOT, so a
    /// restart really does give an engine with no wasm in it. Without it the
    /// boot re-attached the tier at the default threshold AND installed the
    /// bundle's module, so "restart. for a clean engine" was false twice over.
    /// Cleared by any jit_compile that turns the tier back on.</summary>
    internal static bool Disabled;

    /// <summary>The mode a restart comes back with. jit_compile/1 is engine
    /// state, and a restart builds a new engine -- without these, clearing the
    /// database also silently threw away the tier setting the session was
    /// working under, which is the one thing a restart should not touch.
    /// <see cref="Disabled"/> already survived for the off case; these carry
    /// the rest.</summary>
    internal static int BootThreshold = 1;

    /// <summary>Whether the restarted engine batches (see <see
    /// cref="Attach"/>).</summary>
    internal static bool BootBatch = true;

    /// <summary>jit_compile/1 for this page: the threshold alone does not
    /// describe the tier here, because the world is attached lazily and "all"
    /// means "compile the program now and at every consult", not "promote on
    /// the first call". Installed on the STORE so it survives query setup,
    /// and installed even when the tier is off -- otherwise nothing could
    /// turn it back on.</summary>
    internal static void WireJitControl(PrologEngine engine)
        => engine.IlPromotion.JitPolicy = t => SetJit(engine, t).Ok;

    /// <summary>The policy proper. Returns the page's wording too, so
    /// jit_compile and jit_compile cannot drift apart: one of them is the
    /// other's spelling.</summary>
    internal static (bool Ok, string Report) SetJit(PrologEngine engine, int threshold)
    {
        if (!RuntimeCaps.SupportsWasmCodegen)
            return (threshold == 0,
                "% jit_compile: the capability is off in this build\n");
        var store = engine.IlPromotion;
        if (threshold == 0)
        {
            Disabled = true;
            BootThreshold = 0;
            BootBatch = false;
            if (store.Wasm is { } w) { w.Threshold = 0; w.CompileAllOnConsult = false; }
            // The already-promoted go back to bytecode at the next query
            // setup: dropping a delegate with a live choice point inside it
            // would leave the redo nowhere to land.
            store.QueueJitOff();
            return (true, "% jit_compile: off. The goals after this one run on "
                + "Tier-0, and restart. gives an engine with no wasm at all\n");
        }
        Disabled = false;               // asking for any of it un-disables
        bool batch = threshold == 1;    // "all" is the batch; a count is lazy
        BootThreshold = threshold;
        BootBatch = batch;
        if (store.Wasm is null) Attach(engine, threshold, batch);
        if (store.Wasm is not { } wa) return (false, "% jit_compile: could not attach\n");
        wa.Threshold = threshold;
        wa.CompileAllOnConsult = batch;
        if (!batch)
            return (true, $"% jit_compile: threshold={threshold} (lazy; "
                + "jit_compile(all) restores one build per program)\n");
        // The program did not change, the mode did: say so, or the tick
        // takes its early-out and "all" compiles nothing until something else
        // happens to trigger a batch.
        wa.ForceNextBatch();
        long b0 = Stopwatch.GetTimestamp();
        int batched = wa.CompileAllTick(engine);
        double ms = (Stopwatch.GetTimestamp() - b0) * 1000.0 / Stopwatch.Frequency;
        return (true, $"% jit_compile: all -- {batched} predicates compiled now "
            + $"({ms:F0} ms), threshold 1 from here; every consult "
            + "recompiles the new ones\n"
            + (wa.BundleFids.Count == 0 && batched > 100
                ? $"% (the stdlib bundle's wasm is not installed -- {BundleInstallNote})\n"
                : ""));
    }

    /// <summary>Attaches the tier. The default is the BATCH mode: one module
    /// for the whole linked program at each consult boundary; the lazy mode
    /// compiles one module per predicate as it crosses the threshold. Either
    /// way a module is compiled once: a call or a backtrack into a sibling
    /// module is a tail call inside wasm, so the grain costs nothing at run
    /// time.
    ///
    /// <para>A caller asking for a numeric threshold is asking for the lazy
    /// mode and gets it, batch off.</para></summary>
    internal static void Attach(PrologEngine engine, int? threshold = null,
        bool? batch = null)
    {
        int th = threshold ?? BootThreshold;
        bool bt = batch ?? BootBatch;
        if (!RuntimeCaps.SupportsWasmCodegen || Disabled) return;
        var store = engine.IlPromotion;
        var world = new BrowserWasmWorld();
        _world = world;
        var env = new EngineWasmCompileEnv();
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = th,
            CompileAllOnConsult = bt,
            Promoter = (pred, linkedBase) =>
                Promote(store, world, env, pred, linkedBase),
            BatchPromoter = candidates =>
                PromoteBatch(store, world, env, candidates),
            BatchStarting = n =>
            {
                // Only a build worth waiting for gets a line. A handful of
                // predicates compiles faster than the notice takes to read,
                // and the batch may then refuse them all and report zero —
                // the most annoying way to say nothing happened.
                if (n < AnnounceFloor) return;
                AnnouncedBatch = true;
                WebShumwayApp.WriteNoteToPage(
                    $"% compiling {n} predicates to WebAssembly...\n");
            },
        };
        // A relink moved predicates out from under their modules: their rows
        // go to zero (a marker of theirs falls back to bytecode) and the
        // baked callers of each go with it. The modules stay: a re-promotion
        // compiles a fresh one against live addresses.
        store.Wasm.StaleEvicted = world.Evict;
        // A relink moved the code: the world translates at its boundaries.
        store.Wasm.LiveRefreshed = world.RefreshLiveAddresses;
        // A loaded bundle's wasm module (shumway-link --wasm) installs into
        // this world at the next link, instead of compiling its predicates.
        store.Wasm.BundleInstaller = (eng, bytes) => WasmBundleTier.Install(eng, world, bytes);
    }

    /// <summary>The jit_compile(all) path: the whole candidate set in ONE
    /// module. A candidate the compiler refuses must not take the batch
    /// down: on a failed build each candidate is test-compiled alone and the
    /// refusals are marked unpromotable; the survivors build together.</summary>
    private static int PromoteBatch(IlPromotionStore store, BrowserWasmWorld world,
        EngineWasmCompileEnv env,
        List<(CompiledPredicate Pred, int Addr)> candidates)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            var members = new List<WasmGroupMember>();
            foreach (var (pred, addr) in candidates)
                members.Add(new WasmGroupMember(pred, addr,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId)));
            try
            {
                Install(store, world, members, env);
            }
            catch (WasmRegisterException)
            {
                // The BROWSER refused the module — too big, most likely.
                // Nothing was installed; the members are individually fine.
                return 0;
            }
            catch (WasmCompileException)
            {
                // Sort the poisoners out one by one, then build the rest.
                var good = new List<WasmGroupMember>();
                foreach (var m in members)
                {
                    try
                    {
                        WasmPredicateCompiler.CompileGroup(
                            new List<WasmGroupMember> { m }, env);
                        good.Add(m);
                    }
                    catch (WasmCompileException ex)
                    {
                        store.Wasm?.MarkUnpromotable(m.Predicate.FunctorId, ex.Message);
                    }
                }
                if (good.Count == 0) return 0;
                // Individually fine but jointly refused should not happen;
                // if it does, nothing is installed.
                try { Install(store, world, good, env); }
                catch (WasmCompileException) { return 0; }
                members = good;
            }
            foreach (var m in members)
            {
                store.RegisterBoundDelegate(m.Predicate.FunctorId,
                    new WasmTierDelegate(m.Predicate.FunctorId, world).Invoke);
                store.Wasm?.NoteInstalled(m.Predicate.FunctorId, m.Bias,
                    m.Predicate);
            }
            return members.Count;
        }
        finally
        {
            DiagCompileTicks += Stopwatch.GetTimestamp() - t0;
            DiagCompileBuilds++;
        }
    }

    /// <summary>Wall time spent COMPILING modules, and how many — the cost
    /// side of the tier, reported by jit_compile(status). Mono-interpreted
    /// C#, so this is the dominant promotion cost in the browser.</summary>
    internal static long DiagCompileTicks;
    internal static int DiagCompileBuilds;

    private static PredicateDelegate? Promote(IlPromotionStore store,
        BrowserWasmWorld world, EngineWasmCompileEnv env,
        CompiledPredicate pred, int linkedBase)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            var candidate = new WasmGroupMember(pred, linkedBase,
                store.FloatPoolProvider?.Invoke(pred.FunctorId));
            Install(store, world, new List<WasmGroupMember> { candidate }, env);
            return new WasmTierDelegate(pred.FunctorId, world).Invoke;
        }
        catch (WasmRegisterException)
        {
            return null;
        }
        catch (WasmCompileException ex)
        {
            // A refusal is the backend declining a shape it does not
            // translate yet, and that reason is its actionable part.
            store.Wasm?.MarkUnpromotable(pred.FunctorId, ex.Message);
            return null;
        }
        finally
        {
            DiagCompileTicks += Stopwatch.GetTimestamp() - t0;
            DiagCompileBuilds++;
        }
    }

    /// <summary>What became of the stdlib bundle's wasm module at boot —
    /// surfaced by jit_compile(status), because boot-time page writes
    /// predate the console.</summary>
    internal static string BundleInstallNote = "no bundle module";

    // Set by BatchStarting so the tick can close the notice it opened: a
    // "compiling..." with no answer under it reads as a hang.
    internal static bool AnnouncedBatch;

    /// <summary>Batches smaller than this compile without saying so.</summary>
    private const int AnnounceFloor = 5;

    /// <summary>(caller functor, callee functor) to call sites, from the last
    /// module built: the evidence for whether a group could be split along
    /// some module boundary. Static, so it costs nothing at run time.</summary>
    internal static IReadOnlyDictionary<(int Caller, int Callee), int> LastCallSites
        = new Dictionary<(int, int), int>();

    /// <summary>Compiles the members as one module against the id the world
    /// will give it, and installs it. Throws before installing anything.</summary>
    private static void Install(IlPromotionStore store, BrowserWasmWorld world,
        List<WasmGroupMember> members, EngineWasmCompileEnv env)
    {
        var entry = WasmPredicateCompiler.CompileGroup(members, env,
            moduleId: world.NextModuleId);
        var entryAddr = new Dictionary<int, int>(members.Count);
        foreach (var m in members)
            entryAddr[m.Predicate.FunctorId] = m.Bias;
        var displaced = entry.InstallInto(world, entryAddr);
        if (displaced.Count > 0) store.Wasm?.Displaced(displaced);
        LastCallSites = entry.CallSites;
    }
}

/// <summary>The browser measurement: two engines side by side, one with the
/// wasm tier attached at threshold 1 and one plain Tier-0, over the counter,
/// nrev and tak, correctness cross-checked first. Reached via the page hash
/// <c>#wasmtier</c>.</summary>
internal static partial class WebShumwayApp
{
    private const string TierProbeCorpus = """
        loop(0).
        loop(N) :- N > 0, N1 is N - 1, loop(N1).
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).
        nrev([], []).
        nrev([H|T], R) :- nrev(T, RT), app(RT, [H], R).
        range(N, N, [N]) :- !.
        range(I, N, [I|T]) :- I < N, I1 is I + 1, range(I1, N, T).
        tak(X, Y, Z, A) :- X =< Y, !, A = Z.
        tak(X, Y, Z, A) :-
            X1 is X - 1, tak(X1, Y, Z, A1),
            Y1 is Y - 1, tak(Y1, Z, X, A2),
            Z1 is Z - 1, tak(Z1, X, Y, A3),
            tak(A1, A2, A3, A).
        """;

    [JSExport]
    internal static async Task<string> WasmTierProbe(int rounds)
        => await Task.Run(() =>
        {
            var report = new StringBuilder();
            try
            {
                var tiered = new PrologEngine();
                tiered.ConsultString(TierProbeCorpus);
                tiered.IlPromotion.Threshold = 0;
                BrowserWasmTier.Attach(tiered, threshold: 1);
                if (tiered.IlPromotion.Wasm is null)
                    return "wasm tier NOT attached: the capability is off\n";

                var plain = new PrologEngine();
                plain.ConsultString(TierProbeCorpus);

                // Correctness first, on both.
                foreach (var goal in new[]
                {
                    "loop(1000).",
                    "range(1, 30, L), nrev(L, R), R = [30|_], length(R, 30).",
                    "tak(18, 12, 6, 7).",
                })
                {
                    WriteToPage($"[tier] goal {goal}\n");
                    bool a = tiered.Query(goal).Success;
                    WriteToPage($"[tier]   tiered => {a}\n");
                    bool b = plain.Query(goal).Success;
                    report.Append(a && b ? "ok      " : $"MISMATCH tier={a} plain={b} ")
                          .Append(goal).Append('\n');
                    if (!a || !b) return report.ToString();
                }
                int promoted = tiered.IlPromotion.PromotedFunctorIds().Count();
                report.Append("promoted predicates: ").Append(promoted).Append('\n');

                // Attribute nrev: verdict tally + a wall/call/stage time split.
                WasmTierDelegate.ResetDiag();
                BrowserWasmWorld.DiagCallTicks = 0;
                BrowserWasmWorld.DiagStageTicks = 0;
                var dsw = Stopwatch.StartNew();
                tiered.Query("range(1, 200, L), nrev(L, _).");
                dsw.Stop();
                double callMs = BrowserWasmWorld.DiagCallTicks * 1000.0 / Stopwatch.Frequency;
                double stageMs = BrowserWasmWorld.DiagStageTicks * 1000.0 / Stopwatch.Frequency;
                report.Append($"nrev diag: chains={WasmTierDelegate.DiagEntries} "
                    + $"switches={WasmTierDelegate.DiagSwitches} "
                    + $"deopts={WasmTierDelegate.DiagDeopts} "
                    + $"builtins={WasmTierDelegate.DiagBuiltins} "
                    + $"tailexits={WasmTierDelegate.DiagTailExits}\n");
                report.Append($"nrev time: wall={dsw.Elapsed.TotalMilliseconds:F1} ms, "
                    + $"inWasm={callMs:F1} ms, stage={stageMs:F1} ms, "
                    + $"glue={dsw.Elapsed.TotalMilliseconds - callMs - stageMs:F1} ms\n");

                WasmTierDelegate.ResetDiag();
                BrowserWasmWorld.DiagCallTicks = 0;
                BrowserWasmWorld.DiagStageTicks = 0;
                var tsw = Stopwatch.StartNew();
                tiered.Query("tak(14, 10, 4, _).");
                tsw.Stop();
                double tcallMs = BrowserWasmWorld.DiagCallTicks * 1000.0 / Stopwatch.Frequency;
                double tstageMs = BrowserWasmWorld.DiagStageTicks * 1000.0 / Stopwatch.Frequency;
                report.Append($"tak14 diag: chains={WasmTierDelegate.DiagEntries} "
                    + $"switches={WasmTierDelegate.DiagSwitches} "
                    + $"deopts={WasmTierDelegate.DiagDeopts} "
                    + $"builtins={WasmTierDelegate.DiagBuiltins} "
                    + $"tailexits={WasmTierDelegate.DiagTailExits}\n");
                report.Append($"tak14 time: wall={tsw.Elapsed.TotalMilliseconds:F1} ms, "
                    + $"inWasm={tcallMs:F1} ms, stage={tstageMs:F1} ms, "
                    + $"glue={tsw.Elapsed.TotalMilliseconds - tcallMs - tstageMs:F1} ms\n");

                rounds = Math.Max(1, rounds);
                double Median(PrologEngine e, string goal)
                {
                    double best = double.MaxValue;
                    for (int r = 0; r < rounds; r++)
                    {
                        var sw = Stopwatch.StartNew();
                        if (!e.Query(goal).Success) throw new InvalidOperationException(goal);
                        sw.Stop();
                        best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                        // One slow round is answer enough; do not multiply it.
                        if (best > 15_000) break;
                    }
                    return best;
                }
                foreach (var (name, goal) in new (string, string)[]
                {
                    ("counter 300k", "loop(300000)."),
                    ("nrev 200 x5",
                     "range(1, 200, L), nrev(L, _), nrev(L, _), nrev(L, _), nrev(L, _), nrev(L, _)."),
                    ("tak 18,12,6", "tak(18, 12, 6, _)."),
                })
                {
                    WriteToPage($"[tier] measuring {name}\n");
                    double tt = Median(tiered, goal), pp = Median(plain, goal);
                    string line = $"{name}: tier {tt:F1} ms, tier0 {pp:F1} ms, {pp / tt:F1}x";
                    WriteToPage($"[tier] {line}\n");
                    report.Append(line).Append('\n');
                }
            }
            catch (Exception ex)
            {
                report.Append("STOPPED: ").Append(ex.GetType().Name)
                      .Append(": ").Append(ex.Message).Append('\n')
                      .Append(ex.StackTrace).Append('\n');
            }
            return report.ToString();
        }).ConfigureAwait(false);
}

/// <summary>Phase B of the wasm-tier plan: the benchmark page. Five programs
/// — the three of the tier probe plus crypt and zebra from the Van Roy suite
/// — each in its OWN pair of engines (the group module is the program's), a
/// correctness cross-check first, then best-of-rounds wall time for tiered
/// against plain Tier-0 and the geometric mean over the set. Reached via the
/// page hash <c>#wasmbench</c>; the report feeds
/// docs/benchmarks/browser.md.</summary>
internal static partial class WebShumwayApp
{
    private const string CryptSource = """
        crypt([O, N, E, T, W]) :-
            digit(O), O > 0,
            digit(N), N \== O,
            digit(E), E \== O, E \== N,
            digit(T), T > 0, T \== O, T \== N, T \== E,
            digit(W), W \== O, W \== N, W \== E, W \== T,
            100*O + 10*N + E
          + 100*O + 10*N + E
          =:= 100*T + 10*W + O.
        digit(0). digit(1). digit(2). digit(3). digit(4).
        digit(5). digit(6). digit(7). digit(8). digit(9).
        cbench(0) :- !.
        cbench(N) :- crypt(_), !, N1 is N - 1, cbench(N1).
        """;

    private const string ZebraSource = """
        zebra(Houses, Zebra, Water) :-
            Houses = [house(_, _, _, _, _), house(_, _, _, _, _),
                      house(_, _, _, _, _), house(_, _, _, _, _),
                      house(_, _, _, _, _)],
            member_(house(red, english, _, _, _), Houses),
            member_(house(_, spanish, dog, _, _), Houses),
            member_(house(green, _, _, coffee, _), Houses),
            member_(house(_, ukrainian, _, tea, _), Houses),
            right_of(house(green, _, _, _, _), house(ivory, _, _, _, _), Houses),
            member_(house(_, _, snails, _, winston), Houses),
            member_(house(yellow, _, _, _, kools), Houses),
            middle(house(_, _, _, milk, _), Houses),
            first(house(_, norwegian, _, _, _), Houses),
            next_to(house(_, _, _, _, chesterfield), house(_, _, fox, _, _), Houses),
            next_to(house(_, _, _, _, kools), house(_, _, horse, _, _), Houses),
            member_(house(_, _, _, orange_juice, lucky_strike), Houses),
            member_(house(_, japanese, _, _, parliaments), Houses),
            next_to(house(_, norwegian, _, _, _), house(blue, _, _, _, _), Houses),
            member_(house(_, Zebra, zebra, _, _), Houses),
            member_(house(_, Water, _, water, _), Houses).
        member_(X, [X|_]).
        member_(X, [_|T]) :- member_(X, T).
        right_of(A, B, [B, A | _]).
        right_of(A, B, [_|T]) :- right_of(A, B, T).
        next_to(A, B, [A, B | _]).
        next_to(A, B, [B, A | _]).
        next_to(A, B, [_|T]) :- next_to(A, B, T).
        first(X, [X|_]).
        middle(X, [_, _, X, _, _]).
        zbench(0) :- !.
        zbench(N) :- zebra(_, _, _), !, N1 is N - 1, zbench(N1).
        """;

    private sealed record BenchProgram(
        string Name, string Source, string CheckGoal, string BenchGoal);

    private static readonly BenchProgram[] BenchPrograms =
    {
        new("counter 300k", TierProbeCorpus,
            "loop(1000).", "loop(300000)."),
        new("nrev 200 x5", TierProbeCorpus,
            "range(1, 30, L), nrev(L, R), R = [30|_], length(R, 30).",
            "range(1, 200, L), nrev(L, _), nrev(L, _), nrev(L, _), nrev(L, _), nrev(L, _)."),
        new("tak 18,12,6", TierProbeCorpus,
            "tak(18, 12, 6, 7).", "tak(18, 12, 6, _)."),
        new("crypt x10", CryptSource,
            "crypt([O, N, E, T, W]), 200*O + 20*N + 2*E =:= 100*T + 10*W + O.",
            "cbench(10)."),
        new("zebra x10", ZebraSource,
            "zebra(_, Z, W), Z == japanese, W == norwegian.",
            "zbench(10)."),
    };

    [JSExport]
    internal static async Task<string> WasmBenchProbe(int rounds)
        => await Task.Run(() =>
        {
            var report = new StringBuilder();
            rounds = Math.Max(1, rounds);
            var speedups = new List<double>();
            try
            {
                foreach (var prog in BenchPrograms)
                {
                    WriteToPage($"[bench] {prog.Name}: engines up\n");
                    var tiered = new PrologEngine();
                    tiered.ConsultString(prog.Source);
                    tiered.IlPromotion.Threshold = 0;
                    BrowserWasmTier.Attach(tiered, threshold: 1);
                    if (tiered.IlPromotion.Wasm is null)
                        return "wasm tier NOT attached: the capability is off\n";
                    var plain = new PrologEngine();
                    plain.ConsultString(prog.Source);

                    // Correctness on both, and it doubles as the warmup that
                    // promotes the tiered engine's group.
                    bool a = tiered.Query(prog.CheckGoal).Success;
                    bool b = plain.Query(prog.CheckGoal).Success;
                    if (!a || !b)
                    {
                        report.Append($"MISMATCH {prog.Name}: tier={a} plain={b} "
                            + prog.CheckGoal).Append('\n');
                        continue;
                    }

                    WasmTierDelegate.ResetDiag();
                    double tt = BenchMedian(tiered, prog.BenchGoal, rounds);
                    long entries = WasmTierDelegate.DiagEntries;
                    long deopts = WasmTierDelegate.DiagDeopts;
                    long builtins = WasmTierDelegate.DiagBuiltins;
                    long tails = WasmTierDelegate.DiagTailExits;
                    double pp = BenchMedian(plain, prog.BenchGoal, rounds);
                    int promoted = tiered.IlPromotion.PromotedFunctorIds().Count();
                    double speedup = pp / tt;
                    speedups.Add(speedup);
                    string line = $"{prog.Name}: tier {tt:F1} ms, tier0 {pp:F1} ms, "
                        + $"{speedup:F1}x  (promoted={promoted} entries={entries} "
                        + $"deopts={deopts} builtinExits={builtins} tailExits={tails})";
                    WriteToPage($"[bench] {line}\n");
                    report.Append(line).Append('\n');
                }
                if (speedups.Count > 0)
                {
                    double geo = Math.Exp(speedups.Sum(Math.Log) / speedups.Count);
                    report.Append($"geomean over {speedups.Count}: {geo:F1}x\n");
                }
            }
            catch (Exception ex)
            {
                report.Append("STOPPED: ").Append(ex.GetType().Name)
                      .Append(": ").Append(ex.Message).Append('\n')
                      .Append(ex.StackTrace).Append('\n');
            }
            return report.ToString();
        }).ConfigureAwait(false);

    /// <summary>The boards.pl shape: clpfd labeling, where the tier has to
    /// carry a library of hundreds of predicates besides the user's own.
    /// </summary>
    private const string GrainCorpus = """
        :- use_module(library(clpfd)).
        qn(N, Qs) :- length(Qs, N), Qs ins 1..N, all_distinct(Qs), qdiag(Qs).
        qdiag([]).  qdiag([Q|Qs]) :- qoff(Q, Qs, 1), qdiag(Qs).
        qoff(_, [], _).
        qoff(Q, [R|Rs], D) :- Q + D #\= R, R + D #\= Q, D1 is D + 1, qoff(Q, Rs, D1).
        qsolve(N, Qs) :- qn(N, Qs), labeling([ff], Qs), !.
        """;

    /// <summary>clpr: the library whose store is floats and whose hot
    /// builtin is attribute access. On the DESKTOP world it measured far
    /// slower than Tier-0, but that world COPIES the live heap into linear
    /// memory on every chain entry while the browser pins the engine's own
    /// arrays -- so the desktop number says nothing about this one, and this
    /// is the one that counts.</summary>
    private const string ClprCorpus = """
        :- use_module(library(clpr)).
        csolve(0) :- !.
        csolve(N) :- {X + Y =:= 10, X - Y =:= 2}, X =:= 6.0, Y =:= 4.0,
                     M is N - 1, csolve(M).
        """;

    private sealed record GrainProgram(string Name, string Source, string Goal);

    private static readonly GrainProgram[] GrainPrograms =
    {
        new("nrev 200 x5", TierProbeCorpus,
            "range(1, 200, L), nrev(L, _), nrev(L, _), nrev(L, _), nrev(L, _), nrev(L, _)."),
        new("tak 18,12,6", TierProbeCorpus, "tak(18, 12, 6, _)."),
        new("zebra x10", ZebraSource, "zbench(10)."),
        new("clpr x200", ClprCorpus, "csolve(200)."),
        new("clpr x400", ClprCorpus, "csolve(400)."),
    };

    private static GrainProgram QueensProgram(int n)
        => new($"queens {n} (clpfd)", GrainCorpus, $"qsolve({n}, _).");

    /// <summary>The grain measurement: the same programs under the batch
    /// mode (one module per consult) and the lazy mode (one module per
    /// promoted predicate), against Tier-0. What it answers is whether N
    /// small modules cost anything at run time -- hops, host switches,
    /// deopts, the timings -- and what promoting one at a time costs in
    /// compile and registration against the batch, on the same program.
    ///
    /// <para>Every engine here boots the way the PAGE boots: from the stdlib
    /// bundle, with its baked wasm module installed. That is the whole
    /// premise of the comparison, and it used to be simulated (the b* modes
    /// compiled the prelude into one module first) because the bake did not
    /// exist yet. With it real, what the batch compiles at a consult is the
    /// USER's predicates and nothing else.</para></summary>
    [JSExport]
    internal static async Task<string> WasmGrainProbe(int rounds, int queens, string only)
        => await Task.Run(() =>
        {
            var report = new StringBuilder();
            // Leads the report, because everything below is read wrongly
            // without it: a bundle that did not install leaves every
            // predicate to compile LIVE, which shows as a consult an order
            // of magnitude slower and as figures that look like a
            // regression in whatever was changed last.
            report.Append("bundle: ").AppendLine(BrowserWasmTier.BundleInstallNote);
            // And this leads it for the same reason, only worse: without the
            // counters every tally below reads ZERO, and a report of zeros is
            // indistinguishable from a run that never left the module. The
            // only other tell is that `glue` comes out NEGATIVE, since it is
            // what remains after subtracting a delegate figure nothing
            // counted -- which asks the reader to notice a minus sign.
            //
            // Twice now a page was published with the tier flag and not this
            // one, and read as a clean run both times.
            if (!WasmTierDelegate.DiagCompiledIn)
                report.AppendLine(
                    "COUNTERS OFF: this build did not compile them, so every "
                    + "tally below is zero because nobody counted, not because "
                    + "nothing happened. No deopt ranking and no guard "
                    + "histogram. Republish with -p:ShumwayDiag=true beside "
                    + "-p:ShumwayWasmTier=true. The times are real.");
            rounds = Math.Max(1, rounds);
            try
            {
                var programs = queens > 0
                    ? GrainPrograms.Append(QueensProgram(queens)).ToArray()
                    : GrainPrograms;
                foreach (var prog in programs)
                {
                    // only = "<program substring>/<mode>" narrows the matrix
                    // (a hang hunt wants one cell, not the whole grid).
                    string[] sel = only.Split('/');
                    if (sel[0].Length > 0 && !prog.Name.Contains(sel[0])) continue;
                    WriteToPage($"[grain] {prog.Name}\n");
                    report.Append($"== {prog.Name}: {prog.Goal}\n");
                    double tier0 = 0;
                    foreach (string mode in new[] { "tier0", "batch", "eager", "lazy" })
                    {
                        if (sel.Length > 1 && sel[1].Length > 0 && mode != sel[1]) continue;
                        var engine = WebShumwayApp.EngineFromStdlib(announce: false);
                        engine.IlPromotion.Threshold = 0;
                        WasmTierDelegate.ResetDiag();
                        BrowserWasmTier.DiagCompileTicks = 0;
                        BrowserWasmTier.DiagCompileBuilds = 0;
                        BrowserWasmWorld.DiagRegisterTicks = 0;
                        long t0 = Stopwatch.GetTimestamp();
                        if (mode != "tier0")
                        {
                            BrowserWasmTier.Attach(engine, threshold: 1, batch: true);
                            if (engine.IlPromotion.Wasm is null)
                                return "wasm tier NOT attached: the capability is off\n";
                            // The stdlib's baked module, installed as the page
                            // installs it: the grain question is then about the
                            // USER's predicates, which is all that is left to
                            // compile in the browser.
                            WebShumwayApp.InstallBundleWasm(engine);
                            engine.IlPromotion.Wasm.CompileAllOnConsult = mode is "batch";
                        }
                        engine.ConsultString(prog.Source);
                        // The batch compiles at the boundary the page ticks;
                        // eager does the same set one module per predicate;
                        // the lazy mode compiles inside the first run.
                        int batched = mode switch
                        {
                            "batch" => engine.IlPromotion.Wasm!.CompileAllTick(engine),
                            "eager" => engine.IlPromotion.Wasm!.PromoteAllStaticsIndividually(engine),
                            _ => 0,
                        };
                        double consultMs = (Stopwatch.GetTimestamp() - t0) * 1000.0
                                         / Stopwatch.Frequency;
                        var first = Stopwatch.StartNew();
                        bool ok = engine.Query(prog.Goal).Success;
                        if (!ok)
                        {
                            report.Append($"  {mode}: FAILED {prog.Goal}\n");
                            continue;
                        }
                        first.Stop();
                        double compileMs = BrowserWasmTier.DiagCompileTicks * 1000.0
                                         / Stopwatch.Frequency;
                        double registerMs = BrowserWasmWorld.DiagRegisterTicks * 1000.0
                                          / Stopwatch.Frequency;
                        int builds = BrowserWasmTier.DiagCompileBuilds;
                        int promoted = engine.IlPromotion.PromotedFunctorIds().Count();

                        WasmTierDelegate.ResetDiag();
                        Shumway.Interpreter.BytecodeInterpreter.ResetRunTicks();
                        BrowserWasmWorld.DiagCallTicks = 0;
                        BrowserWasmWorld.DiagStageTicks = 0;
                        double best = BenchMedian(engine, prog.Goal, rounds);
                        if (mode == "tier0") tier0 = best;
                        string line = mode == "tier0"
                            ? $"  tier0: {best:F1} ms (first run {first.Elapsed.TotalMilliseconds:F0} ms)"
                            : $"  {mode}: {best:F1} ms, {tier0 / best:F1}x"
                              + $" (first run {first.Elapsed.TotalMilliseconds:F0} ms;"
                              + $" consult {consultMs:F0} ms"
                              + (mode is "batch" or "eager" ? $" incl. {batched} batched" : "") + ")\n"
                              + $"    promoted={promoted} modules={BrowserWasmTier.ModuleCount()}"
                              + $" rows={BrowserWasmTier.ResumeRows()}"
                              + $" bytes={BrowserWasmTier.ModuleBytes():N0}"
                              + $" builds={builds} compile={compileMs:F0} ms register={registerMs:F0} ms\n"
                              + $"    per run: chains={WasmTierDelegate.DiagEntries}"
                              + $" hops={WasmTierDelegate.DiagInWasmHops}"
                              + $" switches={WasmTierDelegate.DiagSwitches}"
                              + $" foreignExits={WasmTierDelegate.DiagForeignExits}"
                              + $" deopts={WasmTierDelegate.DiagDeopts}"
                              + $" builtinExits={WasmTierDelegate.DiagBuiltins}"
                              + $" tailExits={WasmTierDelegate.DiagTailExits}"
                              + $"\n    builtins: " + string.Join(" ",
                                  WasmTierDelegate.BuiltinFailRanking().Take(20)
                                      .Select(r => $"{r.Name}/{r.Arity}={r.Hits}(fail {r.Fails})"))
                              + $"\n    time: wall={best:F0} ms"
                              + $" delegate={WasmTierDelegate.DiagDelegateTicks * 1000.0 / Stopwatch.Frequency:F0}"
                              + $" (inWasm={BrowserWasmWorld.DiagCallTicks * 1000.0 / Stopwatch.Frequency:F0}"
                              + $" stage={BrowserWasmWorld.DiagStageTicks * 1000.0 / Stopwatch.Frequency:F0}"
                              + $" builtins={WasmTierDelegate.DiagBuiltinTicks * 1000.0 / Stopwatch.Frequency:F0}"
                              + $" glue={(WasmTierDelegate.DiagDelegateTicks - BrowserWasmWorld.DiagCallTicks - BrowserWasmWorld.DiagStageTicks - WasmTierDelegate.DiagBuiltinTicks) * 1000.0 / Stopwatch.Frequency:F0})"
                              + $" interp={Shumway.Interpreter.BytecodeInterpreter.DiagRunTicks * 1000.0 / Stopwatch.Frequency - WasmTierDelegate.DiagDelegateTicks * 1000.0 / Stopwatch.Frequency:F0}"
                              + $" setup+answer={best - Shumway.Interpreter.BytecodeInterpreter.DiagRunTicks * 1000.0 / Stopwatch.Frequency:F0} ms"
                              + $" (over {rounds} rounds)"
                              // The two buckets above account for about a
                              // THIRD of the wall time; the rest is the host,
                              // and the deopts are the half of it nothing has
                              // ever attributed. Queens 12 takes 19,029 of
                              // them per run, against 23,594 builtin exits,
                              // and each hands control to the interpreter
                              // rather than just running C# and returning.
                              + "\n" + BrowserWasmTier.DeoptRankingReport(engine).TrimEnd('\n')
                              + "\n" + BrowserWasmTier.ForeignRankingReport(engine) + BrowserWasmTier.BuiltinCallerReport() + BrowserWasmTier.AreaReport() + BrowserWasmTier.TimingReport() + BrowserWasmTier.CompactReport() + BrowserWasmTier.ExhaustionReport().TrimEnd('\n');
                        WriteToPage($"[grain] {line.Replace("\n", " | ")}\n");
                        report.Append(line).Append('\n');
                    }
                }
            }
            catch (Exception ex)
            {
                report.Append("STOPPED: ").Append(ex.GetType().Name)
                      .Append(": ").Append(ex.Message).Append('\n')
                      .Append(ex.StackTrace).Append('\n');
            }
            return report.ToString();
        }).ConfigureAwait(false);

    private static double BenchMedian(PrologEngine e, string goal, int rounds)
    {
        double best = double.MaxValue;
        for (int r = 0; r < rounds; r++)
        {
            var sw = Stopwatch.StartNew();
            if (!e.Query(goal).Success) throw new InvalidOperationException(goal);
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            if (best > 20_000) break;   // one slow round is answer enough
        }
        return best;
    }
}

/// <summary>The REPL's runtime switch for the wasm tier: the page answers the
/// pseudo-goal <c>jit_compile.</c> (and its variants) by calling here, the
/// way <c>restart.</c> is answered by the page. Attaching to the LIVE engine
/// is safe between queries — nothing already running changes, the next
/// dispatches start counting. "off" stops further promotion; what already
/// promoted keeps running as wasm (detaching delegates under a live wasm
/// choice point would break its redo), and <c>restart.</c> is the full off:
/// a fresh engine has no tier attached.</summary>
internal static partial class WebShumwayApp
{
    [JSExport]
    internal static Task<string> JitCompileControl(string command)
        => OnEngine(() =>
        {
            if (!Shumway.Core.RuntimeCaps.SupportsWasmCodegen)
                return "% jit_compile: the capability is off in this build\n";
            var engine = _session?.Engine;
            if (engine is null) return "% jit_compile: no engine\n";
            var store = engine.IlPromotion;

            // The area trace: armed around one stage, dumped after it.
            // Both tiers sample into the same ring, so two dumps of the
            // same goal can be laid side by side.
            if (command == "trace on")
            {
                Shumway.Core.Diagnostics.AreaTrace.Reset();
                Shumway.Core.Diagnostics.AreaTrace.Enabled = true;
                return "% area trace: armed" + System.Environment.NewLine;
            }
            if (command == "trace off")
            {
                Shumway.Core.Diagnostics.AreaTrace.Enabled = false;
                return "% area trace: off" + System.Environment.NewLine;
            }
            if (command == "trace dump")
                return Shumway.Core.Diagnostics.AreaTrace.Dump();

            // The propagation trace: every attribute write, in order.
            if (command == "attrs on")
            {
                Shumway.Core.Diagnostics.AttrTrace.Reset();
                Shumway.Core.Diagnostics.AttrTrace.Enabled = true;
                return "% attr trace: armed" + System.Environment.NewLine;
            }
            if (command == "attrs off")
            {
                Shumway.Core.Diagnostics.AttrTrace.Enabled = false;
                return "% attr trace: off" + System.Environment.NewLine;
            }
            if (command == "attrs dump")
                return Shumway.Core.Diagnostics.AttrTrace.Dump();
            if (command == "live dump")
                return (Shumway.Core.Diagnostics.AttrTrace.LiveSnapshot ?? "(never counted)")
                       + System.Environment.NewLine;

            // What =../2 is handed, call by call.
            if (command == "shapes on")
            {
                Shumway.Core.Diagnostics.CallShapeTrace.Reset();
                Shumway.Core.Diagnostics.CallShapeTrace.Enabled = true;
                return "% call-shape trace: armed" + System.Environment.NewLine;
            }
            if (command == "shapes off")
            {
                Shumway.Core.Diagnostics.CallShapeTrace.Enabled = false;
                return "% call-shape trace: off" + System.Environment.NewLine;
            }
            if (command == "shapes dump")
                return Shumway.Core.Diagnostics.CallShapeTrace.Dump();

            // The attributed-variable CELL lifecycle.
            if (command == "cells on")
            {
                Shumway.Core.Diagnostics.AttVarCellTrace.Reset();
                Shumway.Core.Diagnostics.AttVarCellTrace.Enabled = true;
                return "% cell trace: armed" + System.Environment.NewLine;
            }
            if (command == "cells off")
            {
                Shumway.Core.Diagnostics.AttVarCellTrace.Enabled = false;
                return "% cell trace: off" + System.Environment.NewLine;
            }
            if (command == "cells dump")
                return Shumway.Core.Diagnostics.AttVarCellTrace.Dump();

            // Choice points: pushed, committed to, gone back to.
            if (command == "commits on")
            {
                Shumway.Core.Diagnostics.CommitTrace.Reset();
                Shumway.Core.Diagnostics.CommitTrace.Enabled = true;
                // The EMITTER has to be armed before the modules are
                // compiled, or they carry no trace sites at all.
                Shumway.Compiler.Wasm.WasmPredicateCompiler.TraceCommits = true;
                return "% commit trace: armed" + System.Environment.NewLine;
            }
            if (command == "commits off")
            {
                Shumway.Core.Diagnostics.CommitTrace.Enabled = false;
                return "% commit trace: off" + System.Environment.NewLine;
            }
            if (command == "commits dump")
                return Shumway.Core.Diagnostics.CommitTrace.Dump();



            // The builtin calls IN ORDER. The tier open-codes some, so a
            // raw diff of the two sequences differs for that reason alone;
            // the names are printed so a comparison can drop the open-coded
            // ones from BOTH sides before looking.
            if (command == "seq dump")
            {
                var seq = Shumway.Core.Diagnostics.BuiltinTally.Sequence();
                var sb3 = new System.Text.StringBuilder();
                sb3.Append("# ").Append(seq.Count).Append(" calls in order")
                   .Append(System.Environment.NewLine);
                for (int i = 0; i < seq.Count; i++)
                {
                    string bn;
                    try
                    {
                        var e4 = Shumway.Builtins.BuiltinsRegistry.GetById(seq[i].Id);
                        bn = e4.Name + "/" + e4.Arity;
                    }
                    catch (System.Exception) { bn = "?id" + seq[i].Id; }
                    sb3.Append(i).Append(' ').Append(bn).Append(' ')
                       .Append(seq[i].Cells).Append(System.Environment.NewLine);
                }
                return sb3.ToString();
            }



            // The builtin tally BOTH tiers write, so the same goal gives
            // two numbers that can be subtracted.
            if (command == "builtins on")
            {
                Shumway.Core.Diagnostics.BuiltinTally.Reset();
                Shumway.Core.Diagnostics.BuiltinTally.Enabled = true;
                return "% builtin tally: armed" + System.Environment.NewLine;
            }
            if (command == "builtins off")
            {
                Shumway.Core.Diagnostics.BuiltinTally.Enabled = false;
                return "% builtin tally: off" + System.Environment.NewLine;
            }
            if (command == "builtins dump")
            {
                var snap = Shumway.Core.Diagnostics.BuiltinTally.Snapshot();
                var sb2 = new System.Text.StringBuilder();
                long tot = 0;
                foreach (var (_, c) in snap) tot += c;
                sb2.Append("# ").Append(tot).Append(" builtin calls, ")
                   .Append(snap.Count).Append(" distinct")
                   .Append(System.Environment.NewLine);
                for (int i = 0; i < snap.Count && i < 25; i++)
                {
                    string bn;
                    try
                    {
                        var e3 = Shumway.Builtins.BuiltinsRegistry.GetById(snap[i].Id);
                        bn = e3.Name + "/" + e3.Arity;
                    }
                    catch (System.Exception) { bn = "?id" + snap[i].Id; }
                    sb2.Append(snap[i].Calls).Append(' ').Append(bn)
                       .Append(System.Environment.NewLine);
                }
                return sb2.ToString();
            }


            if (command == "compact reset")
            {
                Shumway.Core.Diagnostics.CompactCensus.Reset();
                WasmTierDelegate.DiagDelegateTicks = 0;
                WasmTierDelegate.DiagBuiltinTicks = 0;
                BrowserWasmWorld.DiagCallTicks = 0;
                BrowserWasmWorld.DiagStageTicks = 0;
                return "% compact census: reset" + System.Environment.NewLine;
            }

            if (command == "status")
            {
                if (store.Wasm is not { } w)
                    return "% jit_compile: not attached (jit_compile. to attach)\n";
                string Name(int f)
                {
                    var (aid, ar) = Shumway.Core.FunctorTable.Lookup(f);
                    return $"{Shumway.Core.AtomTable.GetById(aid)?.Name}/{ar}";
                }
                // Status is read to find the USER's predicates. Everything
                // else folds into counts: the stdlib bundle's members, and
                // library modules (a use_module(library(clpfd)) promotes
                // hundreds of clpfd$... internals under `all`). A name's
                // module is its prefix up to the scope '$' — one more '$'
                // along when it starts with '$' ($q$..., $prelude$$...).
                static string PrefixModuleOf(string name)
                {
                    int at = name.IndexOf('$', name.StartsWith('$') ? 1 : 0);
                    if (at <= 0) return "";
                    int scope = name.StartsWith('$') ? name.IndexOf('$', at + 1) : at;
                    return scope > 0 ? name[..at] : "";
                }
                // A library's EXPORTS carry no module prefix (clpfd's in/2,
                // #=/2, label/1 look exactly like user predicates), so the
                // qualified-name rule alone leaves dozens of them in the
                // list. The engine's module manifests name them.
                var exporter = new Dictionary<int, string>();
                foreach (var (modName, manifest) in engine.Modules)
                    foreach (int pf in manifest.PublicFunctors)
                        exporter[pf] = modName;
                var allPromoted = store.PromotedFunctorIds().ToList();
                var promoted = new List<string>();
                var byModule = new SortedDictionary<string, int>();
                int baked = 0;
                foreach (int f in allPromoted)
                {
                    if (w.BundleFids.Contains(f)) { baked++; continue; }
                    string name = Name(f);
                    string mod = PrefixModuleOf(name);
                    if (mod is "" && exporter.TryGetValue(f, out string? owner)) mod = owner;
                    // Compiler-generated helpers ($disj_N, $neg_N, the $q$
                    // query wrappers) are nobody's source predicate: they
                    // belong to whatever clause spawned them.
                    if (mod is "" or "user" && name.StartsWith('$')) mod = "generated";
                    if (mod is "" or "user") promoted.Add(name);
                    else byModule[mod] = byModule.GetValueOrDefault(mod) + 1;
                }
                var folded = byModule.Select(kv => $"{kv.Value} {kv.Key}").ToList();
                if (baked > 0) folded.Add($"{baked} baked (the stdlib and library bundles)");
                var refused = w.UnpromotableFunctorIds()
                    .Select(f => w.RefusalReason(f) is { } why
                        ? $"{Name(f)} ({why})" : Name(f))
                    .ToList();
                string report = $"% jit_compile: threshold={w.Threshold}\n"
                    + $"%   stdlib bundle wasm: {BrowserWasmTier.BundleInstallNote}\n"
                    + (w.RelinkEvictions > 0
                        ? $"%   relink evictions: {w.RelinkEvictions} (a library "
                          + "load moved the code; evicted predicates re-promote)\n"
                        : "")
                    + $"%   promoted ({promoted.Count}"
                    + (folded.Count > 0 ? " + " + string.Join(" + ", folded) : "")
                    + $"): {string.Join(" ", promoted)}\n"
                    + $"%   refused ({refused.Count}): {string.Join(" ", refused)}\n"
                    + (WasmTierDelegate.DiagCompiledIn
                        ? "%   since the previous status:\n"
                        // Zeros here would read as measurements. They are not:
                        // nobody counted, because a diagnostic does not ship.
                        : "%   counters: NOT COMPILED IN (build with "
                          + "-p:ShumwayDiag=true)\n")
                    + $"%   chains={WasmTierDelegate.DiagEntries} "
                    + $"switches={WasmTierDelegate.DiagSwitches} "
                    + $"hops={WasmTierDelegate.DiagInWasmHops} "
                    + $"foreignExits={WasmTierDelegate.DiagForeignExits} "
                    + $"deopts={WasmTierDelegate.DiagDeopts} "
                    + $"builtinExits={WasmTierDelegate.DiagBuiltins} "
                    + $"tailExits={WasmTierDelegate.DiagTailExits}\n"
                    + $"%   modules={BrowserWasmTier.ModuleCount()}\n"
                    + BrowserWasmTier.DeoptRankingReport(engine)
                    + BrowserWasmTier.ForeignRankingReport(engine) + BrowserWasmTier.BuiltinCallerReport() + BrowserWasmTier.AreaReport() + BrowserWasmTier.TimingReport() + BrowserWasmTier.CompactReport() + BrowserWasmTier.ExhaustionReport()
                    + BrowserWasmTier.BuiltinRankingReport()
                    + WasmCoupling.Report(engine, BrowserWasmTier.LastCallSites)
                    + $"%   compile: {BrowserWasmTier.DiagCompileBuilds} module builds, "
                    + $"{BrowserWasmTier.DiagCompileTicks * 1000.0 / Stopwatch.Frequency:F0} ms"
                    // A build after a consult explains itself; one a predicate
                    // asked for is the one worth chasing, and it needs a name.
                    + (w.LastBatchTrigger.Count > 0
                        ? $", last asked for by {BrowserWasmTier.TriggerNames(w)}\n" : "\n")
                    // The module is registered ONCE PER THREAD, lazily: the
                    // first chain a pool thread opens makes the browser
                    // compile the whole group before it can run anything. On
                    // a big group that is seconds, it reads as the query
                    // hanging after its output, and no other counter here
                    // shows it.
                    + "%   module registration: "
                    + $"{BrowserWasmWorld.DiagRegisterTicks * 1000.0 / Stopwatch.Frequency:F0}"
                    + " ms (per thread, on that thread's first chain)\n";
                // Every counter above is a DELTA SINCE THE PREVIOUS STATUS:
                // cleared on the way out, so goals can be measured one at a
                // time. A running total since boot reads as if it belonged to
                // the last query, and 136 module builds accumulated over a
                // session of them looks exactly like one query gone wrong.
                WasmTierDelegate.ResetDiag();
                BrowserWasmTier.DiagCompileTicks = 0;
                BrowserWasmTier.DiagCompileBuilds = 0;
                BrowserWasmWorld.DiagRegisterTicks = 0;
                return report;
            }
            if (command is "off" or "none")
                return BrowserWasmTier.SetJit(engine, 0).Report;
            if (command == "all")
                return BrowserWasmTier.SetJit(engine, 1).Report;
            // "on", or a numeric threshold. Attach once; afterwards only the
            // threshold moves (re-attaching would abandon the group's members
            // while their delegates live on).
            int threshold = command == "on" ? 16
                : int.TryParse(command, out int n) && n > 0 ? n : -1;
            if (threshold < 0)
                return "% jit_compile: all | none | status | <threshold>\n";
            return BrowserWasmTier.SetJit(engine, threshold).Report;
        });

    /// <summary>Called by the page after a consult and after each completed
    /// query: under jit_compile(all) it re-runs the batch when the program
    /// changed — a consult mid-query included — so the compile always lands
    /// here, on the boundary, never inside the user's next real query. One
    /// int compare when nothing changed.</summary>
    [JSExport]
    internal static Task<int> JitCompileAllTick()
        => OnEngine(() =>
        {
            var engine = _session?.Engine;
            if (engine?.IlPromotion.Wasm is not { CompileAllOnConsult: true } w)
                return 0;
            // The "compiling" half is announced by the store, which is the
            // first place the candidate count is known; here we only report
            // what a build actually did. Nothing to compile says nothing.
            BrowserWasmTier.AnnouncedBatch = false;
            long t0 = Stopwatch.GetTimestamp();
            int n = w.CompileAllTick(engine);
            if (BrowserWasmTier.AnnouncedBatch)
            {
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0
                          / Stopwatch.Frequency;
                // Name the cause. "A build happened" leaves you guessing
                // between the consult you just did and some predicate that
                // asked for one; only the second is worth chasing.
                string why = "after a consult";
                if (w.LastBatchTrigger.Count > 0)
                {
                    var names = new List<string>();
                    foreach (int f in w.LastBatchTrigger)
                    {
                        var (aid, ar) = FunctorTable.Lookup(f);
                        names.Add($"{AtomTable.GetById(aid)?.Name}/{ar}");
                        if (names.Count == 4) break;
                    }
                    why = "asked for by " + string.Join(" ", names)
                        + (w.LastBatchTrigger.Count > 4
                            ? $" and {w.LastBatchTrigger.Count - 4} more" : "");
                }
                WriteNoteToPage($"% compiled ({n} predicates, {ms:F0} ms, {why})\n");
            }
            return n;
        });
}

/// <summary>The BROWSER refused the compiled group module (a V8 limit, an
/// instantiation failure) — the members are individually fine, so retrying
/// them one by one, as a compile refusal warrants, would burn minutes to
/// learn nothing. Callers back the group out instead.</summary>
internal sealed class WasmRegisterException(string message)
    : WasmCompileException(message);
