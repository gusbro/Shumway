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
        long FunctorArityBase);

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
        m[WasmAbi.ViewGen] = CurrentViewGen;
        m[WasmAbi.CutBarrier] = _b0;
        m[WasmAbi.WriteMode] = _writeMode ? 1 : 0;
        m[WasmAbi.UnifyPointer] = _unifyPointer;
        m[WasmAbi.FunctorArityBase] = bases.FunctorArityBase;
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
    }
}
