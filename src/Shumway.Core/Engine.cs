namespace Shumway.Core;

/// <summary>
/// The execution context for a single Prolog computation: heap, stack, registers,
/// trails, and the engine bookkeeping (E/B/P/CP/HB) defined by the WAM.
///
/// Engines are single-threaded internally (no locks on hot paths) and thread-agile
/// (no <c>[ThreadStatic]</c> state) — see ADR-001. Multiple engines coexist in a
/// process and share the global <see cref="AtomTable"/> and <see cref="FunctorTable"/>.
///
/// This file implements the storage substrate (ADR-001/004/005), <see cref="Deref"/>,
/// <see cref="Bind"/> with the HB check and young-to-old rule (ADR-004), and
/// <see cref="Unify"/> for atomic operands. Compound, list, PSTR, float, and the
/// auxiliary-table value tags throw <see cref="NotImplementedException"/> from
/// <see cref="Unify"/> for now; they land with the subsystems that produce them.
/// </summary>
public sealed class Engine
{
    private readonly EngineConfig _config;

    // ----- Heap -----
    private Cell[] _heap;
    private int _heapTop;
    private int _hb;

    // ----- Stack (storage only in this phase; no frame operations yet) -----
    private Cell[] _stack;

    // ----- Registers -----
    private Cell[] _registers;

    // ----- Trails -----
    private int[] _bindingTrail;
    private int _bindingTrailTop;

    private ExtraTrailEntry[] _extraTrail;

    // _stackTop and _extraTrailTop are read-only views of state that will be advanced
    // once stack-frame operations (ADR-005) and extra-trail push (ADR-004) are wired up.
#pragma warning disable CS0649 // assigned by future operations
    private int _stackTop;
    private int _extraTrailTop;
#pragma warning restore CS0649

    // ----- Engine registers (per ADR-005) -----
    // -1 means "none yet" for E and B. P and CP track the program counter and
    // continuation point; they are set when the interpreter is hooked up.
    private int _e = -1;
    private int _b = -1;
    private int _p = -1;
    private int _cp = -1;

    public Engine() : this(new EngineConfig()) { }

    public Engine(EngineConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Validate(config);

        _config = config;
        _heap = new Cell[config.InitialHeapSize];
        _stack = new Cell[config.InitialStackSize];
        _registers = new Cell[config.InitialRegisterCount];
        _bindingTrail = new int[config.InitialBindingTrailSize];
        _extraTrail = new ExtraTrailEntry[config.InitialExtraTrailSize];
    }

    private static void Validate(EngineConfig c)
    {
        if (c.InitialHeapSize <= 0) throw new ArgumentException("InitialHeapSize must be > 0", nameof(c));
        if (c.InitialStackSize <= 0) throw new ArgumentException("InitialStackSize must be > 0", nameof(c));
        if (c.InitialRegisterCount <= 0) throw new ArgumentException("InitialRegisterCount must be > 0", nameof(c));
        if (c.InitialBindingTrailSize <= 0) throw new ArgumentException("InitialBindingTrailSize must be > 0", nameof(c));
        if (c.InitialExtraTrailSize <= 0) throw new ArgumentException("InitialExtraTrailSize must be > 0", nameof(c));
    }

    public EngineConfig Config => _config;

    // ----- Heap accessors -----

    public int HeapTop => _heapTop;
    public int HeapCapacity => _heap.Length;
    public int Hb => _hb;

    public Cell GetHeap(int idx) => _heap[idx];

    /// <summary>Writes a cell directly without trailing. Use for setting up state, not
    /// for binding variables — for that, call <see cref="Bind"/>.</summary>
    public void SetHeap(int idx, Cell value) => _heap[idx] = value;

    /// <summary>Reserves <paramref name="count"/> uninitialised cells on the heap and returns
    /// the index of the first one.</summary>
    public int AllocateHeap(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        EnsureHeapCapacity(count);
        int start = _heapTop;
        _heapTop += count;
        return start;
    }

    /// <summary>Allocates a fresh unbound variable on the heap (a self-pointing REF) and
    /// returns its index.</summary>
    public int AllocateHeapUnbound()
    {
        EnsureHeapCapacity(1);
        int idx = _heapTop;
        _heap[idx] = Cell.UnboundVar(idx);
        _heapTop++;
        return idx;
    }

    // ----- Stack & registers (storage only) -----

    public int StackTop => _stackTop;
    public int StackCapacity => _stack.Length;
    public int RegisterCount => _registers.Length;

    // ----- Trail accessors -----

    public int BindingTrailTop => _bindingTrailTop;
    public int BindingTrailCapacity => _bindingTrail.Length;
    public int ExtraTrailTop => _extraTrailTop;
    public int ExtraTrailCapacity => _extraTrail.Length;

    // ----- Engine register accessors (read-only for now; set by the interpreter later) -----

    public int E => _e;
    public int B => _b;
    public int P => _p;
    public int Cp => _cp;

    // ----- Deref -----

    /// <summary>
    /// Walks REF cells starting at <paramref name="heapIdx"/> until reaching a non-REF cell
    /// or a self-pointing REF (unbound variable). Returns the final heap index. Reading
    /// <see cref="GetHeap"/> at the returned index yields the dereferenced cell.
    /// </summary>
    public int Deref(int heapIdx)
    {
        while (true)
        {
            Cell c = _heap[heapIdx];
            if (c.Tag != Tag.Ref) return heapIdx;
            int target = c.AsHeapIndex;
            if (target == heapIdx) return heapIdx; // unbound: self-pointer
            heapIdx = target;
        }
    }

    // ----- Bind / Trail -----

    /// <summary>
    /// Writes <paramref name="value"/> into the cell at <paramref name="varAddr"/>, trailing
    /// the previous value if the cell pre-dates the most recent choice point
    /// (<c>varAddr &lt; HB</c>). Caller is responsible for ensuring this is a valid binding
    /// (typically the cell is an unbound REF).
    /// </summary>
    public void Bind(int varAddr, Cell value)
    {
        _heap[varAddr] = value;
        if (varAddr < _hb)
            TrailBinding(varAddr);
    }

    private void TrailBinding(int heapIdx)
    {
        EnsureBindingTrailCapacity(1);
        _bindingTrail[_bindingTrailTop++] = heapIdx;
    }

    /// <summary>
    /// Restores all bindings recorded after <paramref name="targetTop"/>, leaving the binding
    /// trail at exactly that depth. Each rolled-back cell becomes a self-pointing REF again.
    /// </summary>
    public void UnwindBindingTrail(int targetTop)
    {
        if (targetTop < 0 || targetTop > _bindingTrailTop)
            throw new ArgumentOutOfRangeException(nameof(targetTop));
        while (_bindingTrailTop > targetTop)
        {
            int idx = _bindingTrail[--_bindingTrailTop];
            _heap[idx] = Cell.UnboundVar(idx);
        }
    }

    // ----- Unify -----

    /// <summary>
    /// Unifies the cells at <paramref name="aIdx"/> and <paramref name="bIdx"/>, dereferencing
    /// each first. Returns <c>true</c> on success, <c>false</c> on failure. On failure the
    /// caller is responsible for unwinding the trail back to the pre-unify state.
    /// </summary>
    public bool Unify(int aIdx, int bIdx)
    {
        int aAddr = Deref(aIdx);
        int bAddr = Deref(bIdx);
        if (aAddr == bAddr) return true;

        Cell aCell = _heap[aAddr];
        Cell bCell = _heap[bAddr];

        if (aCell.Tag == Tag.Ref)
        {
            if (bCell.Tag == Tag.Ref)
                BindVarToVar(aAddr, bAddr);
            else
                BindVarToValue(aAddr, bAddr, bCell);
            return true;
        }
        if (bCell.Tag == Tag.Ref)
        {
            BindVarToValue(bAddr, aAddr, aCell);
            return true;
        }

        // Both bound, neither REF.
        if (aCell.Tag != bCell.Tag) return false;
        return aCell.Tag switch
        {
            Tag.Atom => aCell.AsAtomId == bCell.AsAtomId,
            Tag.Int => aCell.AsInt == bCell.AsInt,
            Tag.Str => UnifyStr(aCell.AsHeapIndex, bCell.AsHeapIndex),
            Tag.Lis => UnifyLis(aCell.AsHeapIndex, bCell.AsHeapIndex),
            Tag.Float => throw new NotImplementedException("Float unification not implemented in this scope."),
            Tag.BigInt => throw new NotImplementedException("BigInt unification not implemented in this scope."),
            Tag.String => throw new NotImplementedException("String unification not implemented in this scope."),
            Tag.Foreign => throw new NotImplementedException("Foreign unification not implemented in this scope."),
            Tag.Pstr => throw new NotImplementedException("PSTR unification not implemented in this scope."),
            _ => throw new InvalidOperationException($"Unify reached cell with unexpected tag {aCell.Tag}."),
        };
    }

    /// <summary>
    /// Unifies two compound (STR) terms. <paramref name="fA"/> and <paramref name="fB"/>
    /// are heap indices of FUNCTOR cells (the payloads of their containing STR cells).
    /// Fails fast if the functor ids differ; otherwise recurses on each argument cell.
    ///
    /// <para>The recursion uses the C# stack, which is sufficient for the typical
    /// (shallow) depths produced by Prolog parsing. Pathologically deep terms — most
    /// notably long cons-cell lists, which is precisely why PSTR exists for strings —
    /// would warrant a future switch to an explicit push-down list.</para>
    /// </summary>
    private bool UnifyStr(int fA, int fB)
    {
        int functorIdA = _heap[fA].AsFunctorId;
        int functorIdB = _heap[fB].AsFunctorId;
        if (functorIdA != functorIdB) return false;
        var (_, arity) = FunctorTable.Lookup(functorIdA);
        for (int i = 1; i <= arity; i++)
        {
            if (!Unify(fA + i, fB + i)) return false;
        }
        return true;
    }

    /// <summary>
    /// Unifies two cons cells. <paramref name="hA"/> and <paramref name="hB"/> are heap
    /// indices of head cells (the payloads of their containing LIS cells); the matching
    /// tail cells live immediately after.
    /// </summary>
    private bool UnifyLis(int hA, int hB)
    {
        if (!Unify(hA, hB)) return false;
        return Unify(hA + 1, hB + 1);
    }

    /// <summary>Young-to-old binding of two unbound variables (ADR-004). The variable with the
    /// higher heap index is bound to a REF pointing at the older one, so the bound cell will
    /// be discarded automatically if the heap is later truncated past the older variable.</summary>
    private void BindVarToVar(int aAddr, int bAddr)
    {
        if (aAddr == bAddr) return;
        if (aAddr < bAddr)
            Bind(bAddr, Cell.Ref(aAddr));
        else
            Bind(aAddr, Cell.Ref(bAddr));
    }

    /// <summary>Binds an unbound variable to a bound value. For compound-valued cells
    /// (STR / LIS / PSTR) the variable receives a REF pointing at the value's heap
    /// position; for atomic values the cell is copied in place (ADR-002 binding policy).</summary>
    private void BindVarToValue(int varAddr, int valueAddr, Cell valueCell)
    {
        if (valueCell.Tag is Tag.Str or Tag.Lis or Tag.Pstr)
            Bind(varAddr, Cell.Ref(valueAddr));
        else
            Bind(varAddr, valueCell);
    }

    // ----- Capacity management -----

    private void EnsureHeapCapacity(int extra)
        => GrowIfNeeded(ref _heap, _heapTop, extra, _config.MaxHeapSize, "heap");

    private void EnsureBindingTrailCapacity(int extra)
        => GrowIfNeeded(ref _bindingTrail, _bindingTrailTop, extra, _config.MaxBindingTrailSize, "binding trail");

    private static void GrowIfNeeded<T>(ref T[] buffer, int top, int extra, int maxSize, string name)
    {
        long required = (long)top + extra;
        if (required <= buffer.Length) return;

        long newSize = buffer.Length;
        while (newSize < required) newSize *= 2;
        if (maxSize > 0 && newSize > maxSize)
        {
            if (required > maxSize)
                throw new InvalidOperationException($"Engine {name} overflow: would need {required} cells, max is {maxSize}.");
            newSize = maxSize;
        }
        if (newSize > int.MaxValue)
            throw new InvalidOperationException($"Engine {name} overflow: would exceed int.MaxValue.");
        Array.Resize(ref buffer, (int)newSize);
    }

    // ----- Internal / test hooks -----

    internal void SetHbForTesting(int hb)
    {
        if (hb < 0 || hb > _heapTop) throw new ArgumentOutOfRangeException(nameof(hb));
        _hb = hb;
    }

    internal ReadOnlySpan<int> BindingTrailSpan => _bindingTrail.AsSpan(0, _bindingTrailTop);
}
