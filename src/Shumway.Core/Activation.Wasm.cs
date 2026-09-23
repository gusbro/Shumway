namespace Shumway.Core;

/// <summary>The wasm-tier state bridge: the mailbox contract of
/// <see cref="WasmAbi"/> filled from and synced back into this activation.
/// The wasm module manipulates the engine's OWN areas through the linear
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
        int AtomMarkerLength = 0);

    /// <summary>Grows the register bank to at least
    /// <paramref name="count"/> registers, BEFORE the runner takes its view:
    /// the compiled module stores X registers by fixed offset and an
    /// out-of-range store would corrupt whatever lies beyond the area.</summary>
    public void EnsureWasmRegisters(int count)
    {
        if (count > _registers.Length) EnsureRegisterCapacity(count);
    }

    // Direct views for the wasm runner (pinning or copying). Unlike
    // Detach*Buffer these do NOT transfer ownership.
    public Cell[] WasmHeapView => _heap;
    public Cell[] WasmStackView => _stack;
    public Cell[] WasmRegistersView => _registers;
    public int[] WasmBindingTrailView => _bindingTrail;

    /// <summary>Grows the binding trail past its current length. For the wasm
    /// tier after a chain deopted AT the trail limit: the wasm limit reserves
    /// a safety margin below the real array, so the interpreter completes the
    /// step inside that margin and the engine never grows the area on its own
    /// -- every later chain would deopt at the same spot, forever.</summary>
    public void GrowWasmBindingTrail()
        => EnsureBindingTrailCapacity(_bindingTrail.Length - _bindingTrailTop + 1);

    /// <summary>Stack counterpart of <see cref="GrowWasmBindingTrail"/>.</summary>
    public void GrowWasmStack()
        => EnsureStackCapacity(_stack.Length - _stackTop + 1);

    /// <summary>Told (module atom, goal functor, RESOLVED functor) every time
    /// the meta-call dispatch resolves a module-tagged goal.
    ///
    /// <para>A compiled module cannot do this resolution: it is a lookup
    /// through the module's locals and imports, and the goal arrives wrapped
    /// as '$mqual'(Module, Goal) precisely because the bare functor is not
    /// the answer. So the HOST publishes what it resolved and the module
    /// reads it -- an inline cache, filled on the slow path it was already
    /// taking.</para>
    ///
    /// <para>The first argument is the ADDRESS MAP the resolution was made
    /// against. A cache of these is only valid for one map -- a new query
    /// links a new one -- which is the same lifetime the engine's own
    /// meta-route cache is stamped with.</para>
    ///
    /// <para>Null unless a wasm world is attached, and called only on the
    /// resolution path, which is already the slow one.</para></summary>
    public System.Action<object?, int, int, int, int, bool>? MetaResolutionObserver;

    /// <summary>False when the activation is in a mode the compiled code does
    /// not honour (trail-everything, occurs_check) -- the tier delegate then
    /// falls back to the predicate's bytecode for the entry.</summary>
    public bool WasmModeCompatible => !_trailEverything && OccursMode == 0;

    /// <summary>Fills the mailbox from the live state. False means
    /// <see cref="WasmModeCompatible"/> is false -- the caller must stay on
    /// the interpreter for this entry.</summary>
    public bool TryFillWasmMailbox(System.Span<long> m, in WasmMailboxBases bases)
    {
        if (!WasmModeCompatible) return false;
        m[WasmAbi.HeapBase] = bases.HeapBase;
        m[WasmAbi.StackBase] = bases.StackBase;
        m[WasmAbi.RegistersBase] = bases.RegistersBase;
        m[WasmAbi.BindingTrailBase] = bases.BindingTrailBase;
        m[WasmAbi.ExtraTrailBase] = 0;
        m[WasmAbi.HeapTop] = _heapTop;
        m[WasmAbi.HeapWatermark] = _gcThreshold > 0
            ? System.Math.Min(_gcThreshold, bases.HeapLimitCells)
            : bases.HeapLimitCells;
        m[WasmAbi.StackTop] = _stackTop;
        m[WasmAbi.ChoiceTop] = _b;
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
        m[WasmAbi.GoalsRun] = 0;                // drained on every sync back
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
        m[WasmAbi.CleanupsPending] = HasPendingCleanups ? 1 : 0;
        return true;
    }

    /// <summary>Adopts the scalars the compiled code synced back on return
    /// (the EmitReturn set, plus ViewGen and the cut barrier, which choice
    /// point restores rewrite in their slots). Pc is NOT adopted here: only
    /// the tail-call and deopt verdicts carry a meaningful Pc, and the
    /// verdict loop applies it explicitly.</summary>
    public void SyncFromWasmMailbox(System.ReadOnlySpan<long> m)
    {
        _heapTop = (int)m[WasmAbi.HeapTop];
        _bindingTrailTop = (int)m[WasmAbi.TrailTop];
        _e = (int)m[WasmAbi.EnvTop];
        _b = (int)m[WasmAbi.ChoiceTop];
        _hb = (int)m[WasmAbi.HeapBacktrack];
        _stackTop = (int)m[WasmAbi.StackTop];
        _cp = (int)m[WasmAbi.ContinuationPc];
        _writeMode = m[WasmAbi.WriteMode] != 0;
        _unifyPointer = (int)m[WasmAbi.UnifyPointer];
        CurrentViewGen = m[WasmAbi.ViewGen];
        _b0 = (int)m[WasmAbi.CutBarrier];
        // What the module did on its own: goals it dispatched and cells it
        // claimed, neither of which passes through a managed counter. Drained
        // here, so re-entering the chain does not count them twice.
        Inferences += m[WasmAbi.GoalsRun];
        _cellsAllocated += m[WasmAbi.CellsClaimed];
    }
}
