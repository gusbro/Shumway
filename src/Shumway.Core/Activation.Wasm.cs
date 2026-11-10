namespace Shumway.Core;

/// <summary>The wasm-tier state bridge: the mailbox contract of
/// <see cref="WasmAbi"/> filled from and synced back into this activation.
/// The wasm module manipulates the engine's own areas through the linear
/// memory (browser: the pinned arrays live inside the runtime's memory;
/// desktop tests: an image the runner copies in and out), so a bail at any
/// verdict leaves the engine exactly where the interpreter would be.</summary>
public sealed partial class Activation
{
    /// <summary>Linear-memory placement of the engine areas plus the guard
    /// limits the compiled code compares against. Limits are element counts
    /// (cells / trail entries), not byte sizes; crossing one deopts and the
    /// managed side grows the array.</summary>
    public readonly record struct WasmMailboxBases(
        long HeapBase, long StackBase, long RegistersBase, long BindingTrailBase,
        int HeapLimitCells, int StackLimitCells, int TrailLimitEntries,
        long FunctorTableBase,
        /// <summary>Base and row count of the resume table, and which module
        /// is running. Zero rows disables in-wasm marker resolution: the
        /// module returns the verdict and the host resolves, which is what it
        /// did before there was a table.</summary>
        long ResumeTableBase = 0,
        int ResumeTableRows = 0,
        long ModuleIndexBase = 0,
        /// <summary>Base and mask of the attribute table's image. A zero base
        /// means there is none and get_attr/3 exits to the host.</summary>
        long TraceBase = 0,
        long TraceLimit = 0,
        long AttrTableBase = 0,
        int AttrTableMask = 0,
        long FdDomFunctorBase = 0,
        int FdDomFunctorLength = 0,
        /// <summary>Base and length of the call-marker table. A zero base
        /// means a meta-call steps aside.</summary>
        long CallMarkerBase = 0,
        int CallMarkerLength = 0,
        /// <summary>Base and mask of the meta-call inline cache. A zero base
        /// means a module-tagged meta-call steps aside.</summary>
        long MetaCacheBase = 0,
        int MetaCacheMask = 0,
        /// <summary>Base and length of the atom marker table.</summary>
        long AtomMarkerBase = 0,
        int AtomMarkerLength = 0,
        /// <summary>Base and length of the attribute trail log's home column.
        /// A zero base means a cut that has to judge an AttrModify entry
        /// steps aside instead of compacting in place.</summary>
        long AttrLogBase = 0,
        int AttrLogLength = 0,
        /// <summary>Base of the extra trail itself. A zero base means a cut
        /// that finds entries on it steps aside instead of compacting.
        /// </summary>
        long ExtraTrailBase = 0,
        /// <summary>Base and capacity of the orphaned-record ring. A zero
        /// base means a compaction that would orphan one declines.</summary>
        long AttrOrphanBase = 0,
        int AttrOrphanLimit = 0,
        /// <summary>Base and length of the arithmetic functor table. A zero
        /// base means a compound operand steps aside.</summary>
        long ArithTableBase = 0,
        int ArithTableLength = 0,
        /// <summary>Base and capacity of the parked attribute writes. A zero
        /// base means an attribute write steps aside.</summary>
        long AttrWriteBase = 0,
        int AttrWriteLimit = 0,
        /// <summary>How many entries the extra trail holds.</summary>
        int ExtraTrailLimitEntries = 0,
        /// <summary>How many rows the module may insert into the attribute
        /// image before the load factor would need a rebuild.</summary>
        int AttrMirrorBudget = 0,
        /// <summary>Base and pair count of the global-variable image.</summary>
        long GlobalVarBase = 0,
        int GlobalVarCount = 0,
        /// <summary>Whether the attribute image at AttrTableBase is the
        /// engine's own array (pinned into linear memory) rather than a copy
        /// of it. Decides who counts the module's inserts: see
        /// AttrMirrorNoteModuleInserts.</summary>
        bool AttrImageShared = false,
        /// <summary>Base and capacity of the dropped-record ring.</summary>
        long AttrDropBase = 0,
        int AttrDropLimit = 0,
        /// <summary>Base and mask of the reverse functor table.</summary>
        long FunctorReverseBase = 0,
        int FunctorReverseMask = 0,
        /// <summary>Base and capacity of the parked wakeups. A zero base
        /// means binding an attributed variable steps aside.</summary>
        long WakeRingBase = 0,
        int WakeRingLimit = 0,
        /// <summary>Base and count of the attribute modules the store has
        /// seen.</summary>
        long AttrModuleBase = 0,
        int AttrModuleCount = 0,
        /// <summary>Base and capacity of the parked findall entries. A zero
        /// base means the collect loop's builtins are requested.</summary>
        long FindallRingBase = 0,
        int FindallRingLimit = 0);

    /// <summary>Grows the register bank to at least
    /// <paramref name="count"/> registers, before the runner takes its view:
    /// the compiled module stores X registers by fixed offset and an
    /// out-of-range store would corrupt whatever lies beyond the area.</summary>
    public void EnsureWasmRegisters(int count)
    {
        if (count > _registers.Length) EnsureRegisterCapacity(count);
    }

    // Direct views for the wasm runner (pinning or copying). Unlike
    // Detach*Buffer these do not transfer ownership.
    public Cell[] WasmHeapView => _heap;
    public Cell[] WasmStackView => _stack;
    public Cell[] WasmRegistersView => _registers;
    public int[] WasmBindingTrailView => _bindingTrail;

    /// <summary>The extra trail, for a world that stages it. Shared so a
    /// cut can compact it in place: unlike every other area here the
    /// module rewrites entries, which is why the copying world has to copy
    /// it back and not just in.</summary>
    public ExtraTrailEntry[] WasmExtraTrailView => _extraTrail;

    /// <summary>Grows the extra trail to leave a module room to append, within
    /// the configured maximum; a world calls it before it takes the view.
    /// The host grows the trail only when a push of its own needs room, and a
    /// module's attribute write reserves two entries it may not write. A
    /// trail that stopped just short of full sent every such write to the
    /// host, which on a young variable trailed nothing, grew nothing, and
    /// left the next write where this one was.</summary>
    public void ReserveWasmExtraTrail()
    {
        int want = _extraTrailTop + WasmExtraTrailHeadroom;
        int max = _config.MaxExtraTrailSize;
        if (max > 0 && want > max) want = max;
        if (want > _extraTrail.Length)
            GrowIfNeeded(ref _extraTrail, _extraTrailTop, want - _extraTrailTop, max, "extra trail");
    }

    /// <summary>The entries <see cref="ReserveWasmExtraTrail"/> leaves free,
    /// the eight a world keeps back included.</summary>
    public const int WasmExtraTrailHeadroom = 256 + 8;

    /// <summary>Where a compaction parks the records it orphaned. Allocated
    /// on first ask, because an engine that never meets a wasm world has no
    /// use for it.</summary>
    public int[] WasmOrphanRingView => _wasmOrphanRing ??= new int[1024];

    private int[]? _wasmOrphanRing;

    /// <summary>Where a module parks the attribute writes it made in the
    /// image, four i32 each: home, module, the old value, the new one.
    /// </summary>
    public int[] WasmAttrWriteRingView
        => _wasmAttrWrites ??= new int[WasmAbi.AttrWriteEntryInts * 512];

    private int[]? _wasmAttrWrites;

    /// <summary>Where a compaction parks the homes whose record must go.
    /// </summary>
    public int[] WasmAttrDropRingView => _wasmAttrDrops ??= new int[512];

    private int[]? _wasmAttrDrops;

    /// <summary>Where a module parks the wakeups of the attributed variables
    /// it binds, <see cref="WasmAbi.WakeEntryInts"/> i32 each: module, the
    /// attribute value, the term bound to, the variable's home.</summary>
    public int[] WasmWakeRingView
        => _wasmWakes ??= new int[WasmAbi.WakeEntryInts * 256];

    private int[]? _wasmWakes;

    /// <summary>Where a module parks findall's collect loop
    /// (<see cref="WasmAbi.FindallRingBase"/>).</summary>
    public long[] WasmFindallRingView => _wasmFindall ??= new long[256];

    private long[]? _wasmFindall;

    /// <summary>The buffers a module opened and the solutions it recorded,
    /// in the order it did. A collect, or any other builtin, runs after this:
    /// every exit drains first.</summary>
    private void DrainParkedFindall(int count)
    {
        if (_wasmFindall is null || count <= 0) return;
        int n = System.Math.Min(count, _wasmFindall.Length);
        for (int i = 0; i < n; i++)
        {
            long v = _wasmFindall[i];
            if (v == 0) PushSolutions(NewSolutions());
            else (_topSolutions ?? throw new InvalidOperationException(
                    "a parked findall solution with no call collecting"))
                .AddToList(this, new Cell(v));
        }
    }

    /// <summary>The wakeups a module parked, queued in the order it bound
    /// them. Nothing else queues inside a chain: an entry with a wakeup
    /// pending runs on bytecode, and a builtin that queues one closes the
    /// chain.</summary>
    private void DrainParkedWakes(int count)
    {
        if (_wasmWakes is null || count <= 0) return;
        int w = WasmAbi.WakeEntryInts;
        int n = count * w <= _wasmWakes.Length ? count : _wasmWakes.Length / w;
        for (int i = 0; i < n; i++)
        {
            int at = i * w;
            _pendingWakeups.Add((_wasmWakes[at], _wasmWakes[at + 1],
                                 _wasmWakes[at + 2], _wasmWakes[at + 3]));
        }
    }

    // Whether the image staged last is the engine's own array, and the
    // insert budget it was staged with: the difference on the way back is
    // how many empty slots the module took.
    private bool _attrImageShared;
    private int _attrMirrorBudgetStaged;

    /// <summary>Drops the records a compaction found dead. Re-checked rather
    /// than trusted: DropDeadAttrRecord asks again whether the cell is still
    /// an attributed variable, and between the module's decision and here a
    /// backtrack may have put one back.</summary>
    private void DrainDroppedAttrRecords(int count)
    {
        if (_wasmAttrDrops is null || count <= 0) return;
        int n = count < _wasmAttrDrops.Length ? count : _wasmAttrDrops.Length;
        for (int i = 0; i < n; i++) DropDeadAttrRecord(_wasmAttrDrops[i]);
    }

    /// <summary>Puts the module's attribute writes in the store, and their
    /// records in the log. The module already wrote the image and the trail
    /// entry -- it had to, both are order-sensitive -- so this is the half
    /// it could not reach, and it must land before anything reads the store.
    ///
    /// <para>The log records go in the same order the module reserved them,
    /// because the trail entries it wrote carry those indices. The assert is
    /// not paranoia: a mismatch would have an unwind restore one attribute's
    /// old value onto another attribute.</para></summary>
    private void DrainPendingAttrWrites(int count)
    {
        if (_wasmAttrWrites is null || count <= 0) return;
        int w = WasmAbi.AttrWriteEntryInts;
        int n = count * w <= _wasmAttrWrites.Length ? count : _wasmAttrWrites.Length / w;
        for (int i = 0; i < n; i++)
        {
            int at = i * w;
            int op = _wasmAttrWrites[at + WasmAbi.AttrWriteOp];
            int home = _wasmAttrWrites[at + WasmAbi.AttrWriteHome];
            int moduleId = _wasmAttrWrites[at + WasmAbi.AttrWriteModule];
            int oldValue = _wasmAttrWrites[at + WasmAbi.AttrWriteOld];
            int newValue = _wasmAttrWrites[at + WasmAbi.AttrWriteNew];

            // The log record goes in the order the module reserved it,
            // because the trail entries it wrote carry those indices.
            int logIndex = _attrTrailLog.Count;
            _attrTrailLog.Add((home, moduleId, oldValue));
            AttrLogMirrorAppend(logIndex, home);

            switch (op)
            {
                case WasmAbi.AttrOpPromote:
                    // The cell is already an attributed variable and the
                    // change is already trailed; what is missing is the
                    // record. Creating it sweeps the image rows of any
                    // orphan the slot left behind -- including the one just
                    // placed, which the set below puts back.
                    AttrCreateRecord(home);
                    AttrSet(home, moduleId, newValue);
                    break;
                case WasmAbi.AttrOpRemove:
                case WasmAbi.AttrOpRemoveLast:
                    // The module tombstoned the image row and, for the last
                    // one, demoted the cell and trailed that too. Only the
                    // store is behind.
                    AttrRemove(home, moduleId);
                    if (op == WasmAbi.AttrOpRemoveLast) AttrDropRecord(home);
                    break;
                default:
                    // AttrSet and not a raw store write: the funnel is what
                    // keeps the image a pure derivation, and it writes the
                    // same value the module already put there.
                    AttrSet(home, moduleId, newValue);
                    break;
            }
        }
    }

    /// <summary>Clears the records a compaction orphaned, which is the write
    /// it could not make itself. Deferred this far and no further: the
    /// chain is out, the engine is authoritative again, and every index was
    /// a live record when the module dropped its entry.</summary>
    private void DrainOrphanedAttrRecords(int count)
    {
        if (_wasmOrphanRing is null || count <= 0) return;
        int n = count < _wasmOrphanRing.Length ? count : _wasmOrphanRing.Length;
        for (int i = 0; i < n; i++)
        {
            int idx = _wasmOrphanRing[i];
            // An unwind since the drop may have truncated the log past it,
            // in which case the record is gone already and better gone.
            if ((uint)idx >= (uint)_attrTrailLog.Count) continue;
            _attrTrailLog[idx] = (int.MinValue, 0, 0);
            AttrLogMirrorSet(idx, int.MinValue);
            Diagnostics.CompactCensus.NoteOrphanCleared();
        }
    }

    /// <summary>How many of those are live, for the same world.</summary>
    public int WasmExtraTrailTop => _extraTrailTop;

    /// <summary>Grows the binding trail past its current length. For the wasm
    /// tier after a chain deopted at the trail limit: the wasm limit reserves
    /// a safety margin below the real array, so the interpreter completes the
    /// step inside that margin and the engine never grows the area on its own
    /// -- every later chain would deopt at the same spot, forever.</summary>
    public void GrowWasmBindingTrail()
        => EnsureBindingTrailCapacity(_bindingTrail.Length - _bindingTrailTop + 1);

    /// <summary>Stack counterpart of <see cref="GrowWasmBindingTrail"/>.</summary>
    public void GrowWasmStack()
        => EnsureStackCapacity(_stack.Length - _stackTop + 1);

    /// <summary>Told (module atom, goal functor, resolved functor) every time
    /// the meta-call dispatch resolves a module-tagged goal.
    ///
    /// <para>A compiled module cannot do this resolution: it is a lookup
    /// through the module's locals and imports, and the goal arrives wrapped
    /// as '$mqual'(Module, Goal) precisely because the bare functor is not
    /// the answer. So the host publishes what it resolved and the module
    /// reads it -- an inline cache, filled on the slow path it was already
    /// taking.</para>
    ///
    /// <para>The first argument is the address map the resolution was made
    /// against. A cache of these is only valid for one map -- a new query
    /// links a new one -- which is the same lifetime the engine's own
    /// meta-route cache is stamped with.</para>
    ///
    /// <para>Null unless a wasm world is attached, and called only on the
    /// resolution path, which is already the slow one.</para></summary>
    /// <para>The builtin id follows when the goal resolved to a direct
    /// builtin (one a module can request), -1 for a predicate; last, the
    /// argument positions <see cref="QualifyMetaArgRegisters"/> qualified,
    /// one bit each, which a module reading the row qualifies the same way.
    /// </para>
    public System.Action<object?, int, int, int, int, bool, int, int>? MetaResolutionObserver;

    /// <summary>Reports a module-tagged meta-call resolution to
    /// <see cref="MetaResolutionObserver"/>, with the meta-arguments the
    /// goal's predicate takes qualified: a row without them would jump
    /// straight to the callee and skip that the next time.</summary>
    public void PublishMetaResolution(object? addresses, int module, int goalKey,
        int appended, int functorId, bool atomGoal, int builtinId)
    {
        if (_goalMetaArgMask < 0) return;
        MetaResolutionObserver?.Invoke(addresses, module, goalKey, appended,
            functorId, atomGoal, builtinId, _goalMetaArgMask);
    }

    /// <summary>ADR-056: the meta-argument positions of the predicate a goal
    /// resolves to (per argument, how many arguments the callee adds, or -1),
    /// by its bare functor id and, when it is the module's own predicate,
    /// the module's name. Set by the embedding at query setup.</summary>
    public Func<int, string?, int[]?>? MetaArgSpecOf { get; set; }

    // The positions the last QualifyMetaArgRegisters qualified, a bit each;
    // -1 when they do not fit the bits, and the resolution is not published.
    private int _goalMetaArgMask;

    /// <summary>Says the goal being dispatched qualifies no meta-argument,
    /// before a dispatch that does not call
    /// <see cref="QualifyMetaArgRegisters"/>.</summary>
    public void NoGoalMetaArgs() => _goalMetaArgMask = 0;

    private static readonly int ColonQualFunctorId =
        FunctorTable.Intern(AtomTable.Intern(":", permanent: true).Id, 2);
    private static readonly int PreludeModuleAtomId =
        AtomTable.Intern("$prelude", permanent: true).Id;
    private static readonly int UserModuleAtomId =
        AtomTable.Intern("user", permanent: true).Id;
    private static readonly int MqualTagFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$mqual", permanent: true).Id, 2);

    /// <summary>ADR-056: a goal meta-called with a module context passes
    /// that module to its meta-arguments, which sit in the argument
    /// registers: <c>G = maplist(ok, L), call(G)</c> in module <c>m</c> runs
    /// <c>maplist(m:ok, L)</c>, since maplist's clauses cannot see
    /// <c>m</c>'s private <c>ok</c>. The compile-time twin is
    /// ModuleRewrite's meta-argument qualification.</summary>
    public void QualifyMetaArgRegisters(int moduleAtomId, int atomId, int totalArity)
    {
        _goalMetaArgMask = 0;
        if (MetaArgSpecOf is not { } specOf) return;
        if (moduleAtomId == PreludeModuleAtomId) return;
        // user's goals carry user too: what a bare name means there is a
        // top-level import first (an engine library's export, ADR-038 second
        // amendment), which the callee's module cannot see. A control
        // construct is not qualified: the caller checks it first, so its
        // culprit stays what the program wrote.
        // The module's own predicate of that name is what the goal reaches.
        string? local = CurrentFunctorAddresses is { } addresses
            && addresses.TryGetValue(ModuleQualify.Mangle(moduleAtomId, atomId, totalArity), out _)
            ? AtomTable.GetById(moduleAtomId)?.Name
            : null;
        int[]? spec = specOf(FunctorTable.Intern(atomId, totalArity), local);
        if (spec is null || spec.Length != totalArity) return;
        bool inUser = moduleAtomId == UserModuleAtomId;
        for (int i = 0; i < totalArity; i++)
        {
            int code = spec[i];
            if (code == -1) continue;
            // As the compile-time twin (ModuleRewrite.QualifyMetaArgs): a `:`
            // argument of a builtin stays bare in user, where bare already
            // names the global store. Wrapped, clause(user:p(1), B) takes the
            // qualified path, which enumerates where the bare one indexes.
            if (inUser && code == ModuleQualify.ColonBuiltinArg) continue;
            _goalMetaArgMask = i < 31 && _goalMetaArgMask >= 0
                ? _goalMetaArgMask | (1 << i) : -1;
            Cell c = GetRegister(i);
            Cell d = c.Tag == Tag.Ref ? GetHeap(Deref(c.AsHeapIndex)) : c;
            if (d.Tag == Tag.Str)
            {
                int f = GetHeap(d.AsHeapIndex).AsFunctorId;
                if (f == ColonQualFunctorId || f == MqualTagFunctorId) continue;
            }
            // An attributed variable copied out of the goal term keeps its
            // one home: the new cell refers to it.
            if (c.Tag == Tag.AttVar) c = Cell.Ref(c.AsHeapIndex);
            int h = AllocateHeap(3);
            SetHeap(h, Cell.Functor(ColonQualFunctorId));
            SetHeap(h + 1, Cell.Atom(moduleAtomId));
            SetHeap(h + 2, c);
            SetRegister(i, Cell.Str(h));
        }
    }

    /// <summary>False when the activation is in a mode the compiled code does
    /// not honour (trail-everything, occurs_check) -- the tier delegate then
    /// falls back to the predicate's bytecode for the entry.</summary>
    public bool WasmModeCompatible => !_trailEverything && OccursMode == 0;

    /// <summary>Fills the mailbox from the live state. False means
    /// <see cref="WasmModeCompatible"/> is false -- the caller must stay on
    /// the interpreter for this entry.</summary>
    /// <summary>Time the staging's diagnostic checks took (the attribute
    /// mirror and log verified against their stores, whole). A timing split
    /// has to take it out of the staging: it grows with the attribute table
    /// and a release build does not pay it. Diagnostic.</summary>
    public static long DiagStagingVerifyTicks;
#if SHUMWAY_DIAG
    private int _stagingsSinceVerify;
#endif

    public bool TryFillWasmMailbox(System.Span<long> m, in WasmMailboxBases bases)
    {
        if (!WasmModeCompatible) return false;
        m[WasmAbi.HeapBase] = bases.HeapBase;
        m[WasmAbi.StackBase] = bases.StackBase;
        m[WasmAbi.RegistersBase] = bases.RegistersBase;
        m[WasmAbi.BindingTrailBase] = bases.BindingTrailBase;
        // Withheld under a debug session, and that is ADR-035 D5+: with
        // TrailEverything on, the trail is the debugger's history and
        // cut-time compaction destroys it. Not offering the image is how
        // the module is told, because a module with no base cannot
        // compact and steps aside exactly as it did before.
        m[WasmAbi.ExtraTrailBase] = _trailEverything ? 0 : bases.ExtraTrailBase;
        m[WasmAbi.HeapTop] = _heapTop;
        m[WasmAbi.HeapWatermark] = _gcThreshold > 0
            ? System.Math.Min(_gcThreshold, bases.HeapLimitCells)
            : bases.HeapLimitCells;
        m[WasmAbi.StackTop] = _stackTop;
        m[WasmAbi.ChoiceTop] = _b;
        m[WasmAbi.BacktrackFloor] = _backtrackFloor;
        m[WasmAbi.HeapBacktrack] = _hb;
        m[WasmAbi.TrailTop] = _bindingTrailTop;
        m[WasmAbi.Flags] = (HasPendingWakeups ? WasmAbi.FlagWakeupPending : 0)
                         | (IsCancellationRequested ? WasmAbi.FlagInterrupt : 0);
        m[WasmAbi.Pc] = 0;
        m[WasmAbi.BuiltinId] = 0;
        m[WasmAbi.Cursor] = 0;
        m[WasmAbi.EnvTop] = _e;
        m[WasmAbi.ContinuationPc] = _cp;
        m[WasmAbi.StackLimit] = bases.StackLimitCells;
        m[WasmAbi.TrailLimit] = bases.TrailLimitEntries;
        m[WasmAbi.ExtraTrailTop] = _extraTrailTop;
        m[WasmAbi.CellsClaimed] = 0;
        m[WasmAbi.ResumeTableBase] = bases.ResumeTableBase;
        m[WasmAbi.ResumeTableLength] = bases.ResumeTableRows;
        m[WasmAbi.ModuleIndexBase] = bases.ModuleIndexBase;
        m[WasmAbi.ViewGen] = CurrentViewGen;
        m[WasmAbi.CutBarrier] = _b0;
        m[WasmAbi.WriteMode] = _writeMode ? 1 : 0;
        m[WasmAbi.UnifyPointer] = _unifyPointer;
        m[WasmAbi.FunctorTableBase] = bases.FunctorTableBase;
        m[WasmAbi.TraceBase] = bases.TraceBase;
        m[WasmAbi.TraceLimit] = bases.TraceLimit;
        m[WasmAbi.TraceTop] = 0;
        m[WasmAbi.AttrTableBase] = bases.AttrTableBase;
        m[WasmAbi.AttrTableMask] = bases.AttrTableMask;
        m[WasmAbi.FdDomFunctorBase] = bases.FdDomFunctorBase;
        m[WasmAbi.FdDomFunctorLength] = bases.FdDomFunctorLength;
        m[WasmAbi.CallMarkerBase] = bases.CallMarkerBase;
        m[WasmAbi.CallMarkerLength] = bases.CallMarkerLength;
        m[WasmAbi.MetaCacheBase] = bases.MetaCacheBase;
        m[WasmAbi.MetaCacheMask] = bases.MetaCacheMask;
        m[WasmAbi.AtomMarkerBase] = bases.AtomMarkerBase;
        m[WasmAbi.AtomMarkerLength] = bases.AtomMarkerLength;
        m[WasmAbi.CleanupReach] = WasmCleanupReach;
        m[WasmAbi.AttrWriteBase] = bases.AttrWriteBase;
        m[WasmAbi.AttrWriteLimit] = bases.AttrWriteLimit;
        m[WasmAbi.AttrWriteTop] = 0;
        m[WasmAbi.ExtraTrailLimit] = bases.ExtraTrailLimitEntries;
        m[WasmAbi.AttrMirrorBudget] = bases.AttrMirrorBudget;
        m[WasmAbi.FunctorReverseBase] = bases.FunctorReverseBase;
        m[WasmAbi.FunctorReverseMask] = bases.FunctorReverseMask;
        m[WasmAbi.AttrDropBase] = bases.AttrDropBase;
        m[WasmAbi.AttrDropLimit] = bases.AttrDropLimit;
        m[WasmAbi.AttrDropTop] = 0;
        m[WasmAbi.WakeRingBase] = bases.WakeRingBase;
        m[WasmAbi.WakeRingTop] = 0;
        m[WasmAbi.WakeRingLimit] = bases.WakeRingLimit;
        m[WasmAbi.AttrModuleBase] = bases.AttrModuleBase;
        m[WasmAbi.AttrModuleCount] = bases.AttrModuleCount;
        m[WasmAbi.FindallRingBase] = bases.FindallRingBase;
        m[WasmAbi.FindallRingTop] = 0;
        m[WasmAbi.FindallRingLimit] = bases.FindallRingLimit;
        m[WasmAbi.ArithTableBase] = bases.ArithTableBase;
        m[WasmAbi.ArithTableLength] = bases.ArithTableLength;
        m[WasmAbi.ArithValue] = 0;
        m[WasmAbi.ArithKind] = 0;
        m[WasmAbi.AttrOrphanBase] = bases.AttrOrphanBase;
        m[WasmAbi.AttrOrphanLimit] = bases.AttrOrphanLimit;
        m[WasmAbi.AttrOrphanTop] = 0;
        m[WasmAbi.AttrLogBase] = bases.AttrLogBase;
        m[WasmAbi.AttrLogLength] = bases.AttrLogLength;
        m[WasmAbi.AttrRecordCount] = AttrTableCount;
        m[WasmAbi.GlobalVarBase] = bases.GlobalVarBase;
        m[WasmAbi.GlobalVarCount] = bases.GlobalVarCount;
        _attrImageShared = bases.AttrImageShared;
        _attrMirrorBudgetStaged = bases.AttrMirrorBudget;
        m[WasmAbi.CompactBarrier] = _compactBarrier;
        m[WasmAbi.CompactParentBinding] = _compactParentBinding;
        m[WasmAbi.CompactParentExtra] = _compactParentExtra;
        m[WasmAbi.CompactedBinding] = _compactedBinding;
        m[WasmAbi.CompactedExtra] = _compactedExtra;

        // What a compaction owes the catch frames, as three numbers. They
        // cannot change inside a chain: a frame is pushed and deactivated by
        // '$catch_begin'/2 and '$catch_end'/0, which are builtins, and a
        // builtin ends the chain.
        //
        // The floor is a survival floor, not an optimisation: a throw
        // truncates the heap only to its own snapshot, so every mutation of
        // a cell older than that has to be restorable when it fires. The two
        // maxima are the opposite question -- a compaction that leaves both
        // trail tops at or above them clipped no snapshot, and clipping is
        // control state a module cannot write.
        int catchFloor = 0, snapBind = 0, snapExtra = 0;
        for (int i = 0; i < _catchFrames.Count; i++)
        {
            CatchFrame f = _catchFrames[i];
            if (f.Active && f.SnapHeapTop > catchFloor) catchFloor = f.SnapHeapTop;
            if (f.SnapBindingTrailTop > snapBind) snapBind = f.SnapBindingTrailTop;
            if (f.SnapExtraTrailTop > snapExtra) snapExtra = f.SnapExtraTrailTop;
        }
        // Armed here rather than in each world: every staging is a
        // handover, and the image is what the module reads. A divergence
        // from the store is a module computing on a lie, and this is the
        // last moment it costs nothing to catch. Diagnostic builds only.
        //
        // Amortized: the check walks the whole table, which a search grows
        // (clp(Z) sudoku: ~3 ms a staging, most of a two-minute run), so it
        // runs once per table-size/64 stagings. A small table, every test's,
        // is still checked at every one.
#if SHUMWAY_DIAG
        if (++_stagingsSinceVerify >= System.Math.Max(1, AttrTableCount / 64))
        {
            _stagingsSinceVerify = 0;
            long tv = System.Diagnostics.Stopwatch.GetTimestamp();
            AttrMirrorVerify("wasm staging");
            AttrLogMirrorAssert();
            DiagStagingVerifyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tv;
        }
#endif
        m[WasmAbi.CatchHeapFloor] = catchFloor;
        m[WasmAbi.CatchSnapBindingMax] = snapBind;
        m[WasmAbi.CatchSnapExtraMax] = snapExtra;
        return true;
    }

    /// <summary>Adopts the scalars the compiled code synced back on return
    /// (the EmitReturn set, plus ViewGen and the cut barrier, which choice
    /// point restores rewrite in their slots). Pc is not adopted here: only
    /// the tail-call and deopt verdicts carry a meaningful Pc, and the
    /// verdict loop applies it explicitly.</summary>
    public void SyncFromWasmMailbox(System.ReadOnlySpan<long> m)
    {
        // The cells the module claimed on its own, which no managed counter
        // saw, less the ones still on the heap: what its backtracking gave
        // back. Drained here, so re-entering the chain does not count them twice.
        int heapTop = (int)m[WasmAbi.HeapTop];
        _cellsReclaimed += m[WasmAbi.CellsClaimed] - (heapTop - _heapTop);
        _heapTop = heapTop;
        _bindingTrailTop = (int)m[WasmAbi.TrailTop];
        // Adopted because a cut that compacts in place lowers it. Nothing
        // else in the module writes it, so this is a no-op for every other
        // path -- and leaving it unadopted would silently undo the
        // compaction on the way out.
        _extraTrailTop = (int)m[WasmAbi.ExtraTrailTop];
        // The empty slots the module took, before the drain: a put in the
        // drain that crosses the load factor rebuilds and recounts, and
        // the recount must see them as occupied.
        if (_attrImageShared)
        {
            AttrMirrorNoteModuleInserts(_attrMirrorBudgetStaged - (int)m[WasmAbi.AttrMirrorBudget]);
            _attrMirrorBudgetStaged = (int)m[WasmAbi.AttrMirrorBudget];
        }
        // Writes before orphans, and the order is load-bearing: a
        // compaction in the same chain can have orphaned a record this
        // write created, and clearing an index the log does not hold yet
        // does nothing at all.
        DrainPendingAttrWrites((int)m[WasmAbi.AttrWriteTop]);
        DrainOrphanedAttrRecords((int)m[WasmAbi.AttrOrphanTop]);
        DrainDroppedAttrRecords((int)m[WasmAbi.AttrDropTop]);
        DrainParkedWakes((int)m[WasmAbi.WakeRingTop]);
        DrainParkedFindall((int)m[WasmAbi.FindallRingTop]);
        // The compaction watermark, which a module's walk moves.
        _compactBarrier = (int)m[WasmAbi.CompactBarrier];
        _compactParentBinding = (int)m[WasmAbi.CompactParentBinding];
        _compactParentExtra = (int)m[WasmAbi.CompactParentExtra];
        _compactedBinding = (int)m[WasmAbi.CompactedBinding];
        _compactedExtra = (int)m[WasmAbi.CompactedExtra];
        _e = (int)m[WasmAbi.EnvTop];
        _b = (int)m[WasmAbi.ChoiceTop];
        _hb = (int)m[WasmAbi.HeapBacktrack];
        _stackTop = (int)m[WasmAbi.StackTop];
        _cp = (int)m[WasmAbi.ContinuationPc];
        _writeMode = m[WasmAbi.WriteMode] != 0;
        _unifyPointer = (int)m[WasmAbi.UnifyPointer];
        CurrentViewGen = m[WasmAbi.ViewGen];
        _b0 = (int)m[WasmAbi.CutBarrier];
    }
}
