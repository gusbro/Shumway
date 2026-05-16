using System.Numerics;

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

    // ----- Per-engine auxiliary value tables (ADR-002) -----
    private readonly List<BigInteger> _bigIntTable = new();
    private readonly List<string> _stringTable = new();
    private readonly List<object?> _foreignTable = new();

    private int _stackTop;
    private int _extraTrailTop;

    // ----- Engine registers (per ADR-005) -----
    // -1 means "none yet" for E, B, and B0. P and CP track the program counter and
    // continuation point; they are set when the interpreter is hooked up. B0 is
    // the value of B at the most recent procedure entry — neck_cut uses it as the
    // implicit cut barrier so the call protocol can distinguish CPs created inside
    // the current procedure from CPs that pre-existed it.
    private int _e = -1;
    private int _b = -1;
    private int _b0 = -1;
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

    // ----- Stack & registers -----

    public int StackTop => _stackTop;
    public int StackCapacity => _stack.Length;
    public int RegisterCount => _registers.Length;

    /// <summary>Reads a raw cell from the stack. The interpretation depends on context —
    /// frame control slots (CE / CP / B / BP / trail tops) hold raw <c>long</c> values
    /// rather than tagged cells; argument slots inside choice points hold A-register
    /// snapshots; Y-slots hold permanent variables.</summary>
    public Cell GetStack(int idx) => _stack[idx];

    // ----- Environment frame layout (ADR-005) -----

    /// <summary>Offset of <c>CE</c> (previous environment) within a frame.</summary>
    public const int EnvCeOffset = 0;

    /// <summary>Offset of <c>CP</c> (saved continuation point) within a frame.</summary>
    public const int EnvCpOffset = 1;

    /// <summary>Offset of <c>Y1</c> within a frame. <c>Yk</c> is at <c>EnvY1Offset + (k-1)</c>.</summary>
    public const int EnvY1Offset = 2;

    /// <summary>Size in cells of an environment frame with <paramref name="numPermanents"/> Y slots.</summary>
    public static int EnvSize(int numPermanents) => 2 + numPermanents;

    /// <summary>
    /// Pushes an environment frame with <paramref name="numPermanents"/> Y slots onto the
    /// stack, saving the current <see cref="E"/> as CE and <see cref="Cp"/> as CP. The Y
    /// slots are initialised to a self-pointing REF as an "uninitialised" marker; the
    /// compiler guarantees each Y is written by an instruction (e.g. <c>get_variable</c>)
    /// before any read, so this marker is never actually dereferenced. After the call
    /// <see cref="E"/> points at the new frame.
    /// </summary>
    public void Allocate(int numPermanents)
    {
        if (numPermanents < 0)
            throw new ArgumentOutOfRangeException(nameof(numPermanents));

        int frameSize = EnvSize(numPermanents);
        EnsureStackCapacity(frameSize);

        int newE = _stackTop;
        _stack[newE + EnvCeOffset] = new Cell(_e);
        _stack[newE + EnvCpOffset] = new Cell(_cp);
        // Y slots are initialised as REFs to fresh heap-unbound variables. Earlier drafts
        // used a stack-self-pointing REF as an "uninitialised marker", but that complicates
        // unify (the REF target would be a stack address rather than a heap index). Going
        // through the heap on first allocation costs one extra heap cell per permanent
        // but lets Deref/Bind/Unify treat permanents and ordinary variables uniformly.
        for (int i = 0; i < numPermanents; i++)
        {
            int slot = newE + EnvY1Offset + i;
            int heapIdx = AllocateHeapUnbound();
            _stack[slot] = Cell.Ref(heapIdx);
        }
        _stackTop = newE + frameSize;
        _e = newE;
    }

    /// <summary>
    /// Restores <see cref="Cp"/> and <see cref="E"/> from the current frame. Does NOT
    /// reduce <see cref="StackTop"/> — the WAM convention is to leave the popped frame
    /// in place until a subsequent op (e.g. <c>trust_me</c> or the equivalent reclamation
    /// pass) determines it is safe to shrink the stack.
    /// </summary>
    public void Deallocate()
    {
        if (_e < 0)
            throw new InvalidOperationException("Deallocate called without an active environment frame.");
        _cp = (int)_stack[_e + EnvCpOffset].Data;
        _e = (int)_stack[_e + EnvCeOffset].Data;
    }

    /// <summary>Reads the <c>Y(k+1)</c> slot of the current environment frame.</summary>
    public Cell GetY(int slot)
    {
        if (_e < 0)
            throw new InvalidOperationException("No environment frame is active.");
        return _stack[_e + EnvY1Offset + slot];
    }

    /// <summary>Writes the <c>Y(k+1)</c> slot of the current environment frame.</summary>
    public void SetY(int slot, Cell value)
    {
        if (_e < 0)
            throw new InvalidOperationException("No environment frame is active.");
        _stack[_e + EnvY1Offset + slot] = value;
    }

    // ----- Choice-point frame layout (ADR-005) -----
    //
    // Layout: [arity | A1 .. Aarity | CE | CP | B | BP | BindingTrailTop |
    //          ExtraTrailTop | HeapTop | Hb]
    // Total size = 9 + arity cells.

    public const int CpArityOffset = 0;
    public const int CpArg1Offset = 1;

    public static int CpCeOffset(int arity) => 1 + arity;
    public static int CpCpOffset(int arity) => 1 + arity + 1;
    public static int CpBOffset(int arity) => 1 + arity + 2;
    public static int CpBpOffset(int arity) => 1 + arity + 3;
    public static int CpBindingTrailOffset(int arity) => 1 + arity + 4;
    public static int CpExtraTrailOffset(int arity) => 1 + arity + 5;
    public static int CpHeapTopOffset(int arity) => 1 + arity + 6;
    public static int CpHbOffset(int arity) => 1 + arity + 7;

    /// <summary>Size in cells of a choice-point frame with <paramref name="arity"/> saved args.</summary>
    public static int CpSize(int arity) => 9 + arity;

    /// <summary>
    /// Pushes a choice point onto the stack, snapshotting the first <paramref name="arity"/>
    /// argument registers and the engine's continuation/trail/heap state. After the call
    /// <see cref="B"/> points at the new CP and <see cref="Hb"/> is bumped to the current
    /// <see cref="HeapTop"/> so that subsequent bindings of pre-CP heap cells get trailed.
    /// </summary>
    public void PushChoicePoint(int arity, int nextClauseAddr)
    {
        if (arity < 0)
            throw new ArgumentOutOfRangeException(nameof(arity));
        if (arity > _registers.Length)
            throw new ArgumentOutOfRangeException(nameof(arity),
                $"Arity {arity} exceeds register capacity {_registers.Length}.");

        int size = CpSize(arity);
        EnsureStackCapacity(size);

        int newB = _stackTop;
        _stack[newB + CpArityOffset] = new Cell(arity);
        for (int i = 0; i < arity; i++)
            _stack[newB + CpArg1Offset + i] = _registers[i];

        _stack[newB + CpCeOffset(arity)] = new Cell(_e);
        _stack[newB + CpCpOffset(arity)] = new Cell(_cp);
        _stack[newB + CpBOffset(arity)] = new Cell(_b);
        _stack[newB + CpBpOffset(arity)] = new Cell(nextClauseAddr);
        _stack[newB + CpBindingTrailOffset(arity)] = new Cell(_bindingTrailTop);
        _stack[newB + CpExtraTrailOffset(arity)] = new Cell(_extraTrailTop);
        _stack[newB + CpHeapTopOffset(arity)] = new Cell(_heapTop);
        _stack[newB + CpHbOffset(arity)] = new Cell(_hb);

        _stackTop = newB + size;
        _b = newB;
        _hb = _heapTop;
    }

    /// <summary>
    /// Restores engine state from the current choice point and updates its BP slot to
    /// <paramref name="nextClauseAddr"/>. The CP itself is preserved so subsequent failures
    /// will retry against the new <c>nextClauseAddr</c>. <see cref="Hb"/> is reset to the
    /// just-restored <see cref="HeapTop"/> — the CP is still active so its heap boundary
    /// remains in effect.
    /// </summary>
    public void RetryMeElse(int nextClauseAddr)
    {
        if (_b < 0)
            throw new InvalidOperationException("RetryMeElse called without an active choice point.");
        int arity = RestoreCommonFromCurrentCp();
        _hb = _heapTop;
        _stack[_b + CpBpOffset(arity)] = new Cell(nextClauseAddr);
    }

    /// <summary>
    /// Restores engine state from the current choice point and discards it: <see cref="B"/>
    /// reverts to the previous CP and the stack slots occupied by the discarded CP (plus
    /// any frames above it) are reclaimed. <see cref="Hb"/> is reset to the value saved
    /// when this CP was pushed — i.e., the boundary of the now-current (previous) CP.
    /// </summary>
    public void TrustMe()
    {
        if (_b < 0)
            throw new InvalidOperationException("TrustMe called without an active choice point.");
        int arity = RestoreCommonFromCurrentCp();
        _hb = (int)_stack[_b + CpHbOffset(arity)].Data;
        int oldB = _b;
        _b = (int)_stack[_b + CpBOffset(arity)].Data;
        _stackTop = oldB;
    }

    /// <summary>Restores registers, E, CP, trails, and HeapTop from the current CP. Returns
    /// the saved arity. Does NOT set <see cref="Hb"/> — that differs between Retry and Trust
    /// and is the caller's responsibility.</summary>
    private int RestoreCommonFromCurrentCp()
    {
        int arity = (int)_stack[_b + CpArityOffset].Data;

        for (int i = 0; i < arity; i++)
            _registers[i] = _stack[_b + CpArg1Offset + i];

        _e = (int)_stack[_b + CpCeOffset(arity)].Data;
        _cp = (int)_stack[_b + CpCpOffset(arity)].Data;

        int bindingTarget = (int)_stack[_b + CpBindingTrailOffset(arity)].Data;
        int extraTarget = (int)_stack[_b + CpExtraTrailOffset(arity)].Data;
        UnwindTrails(bindingTarget, extraTarget);

        _heapTop = (int)_stack[_b + CpHeapTopOffset(arity)].Data;
        return arity;
    }

    // ----- Cut -----

    /// <summary>
    /// Discards every choice point above <paramref name="barrier"/>, then compacts the
    /// binding and extra trails: entries above the parent CP's saved trail tops that bind
    /// or reference heap cells created after the parent's heap top are dropped (those
    /// cells will be truncated on any outer backtrack, so the trail entries serve no
    /// purpose). For each surviving extra-trail entry the <c>BindingTrailMarker</c> is
    /// adjusted to the compacted binding-trail position so the interleaved unwind from
    /// <see cref="UnwindTrails"/> still processes mutations in the right order.
    ///
    /// <para>If <paramref name="barrier"/> is <c>-1</c>, every remaining CP is discarded
    /// and the trails are fully compacted with parent-heap-top = 0 — i.e. emptied,
    /// since nothing can backtrack past a cut to "no choice point at clause entry".</para>
    /// </summary>
    public void Cut(int barrier)
    {
        if (barrier < -1)
            throw new ArgumentOutOfRangeException(nameof(barrier));
        if (barrier > _b)
            throw new ArgumentOutOfRangeException(nameof(barrier),
                $"Barrier {barrier} is above current B {_b}; cannot cut upward.");
        if (_b == barrier)
            return;

        _b = barrier;

        int parentBindingTop, parentExtraTop, parentHeapTop;
        if (_b < 0)
        {
            parentBindingTop = 0;
            parentExtraTop = 0;
            parentHeapTop = 0;
        }
        else
        {
            int arity = (int)_stack[_b + CpArityOffset].Data;
            parentBindingTop = (int)_stack[_b + CpBindingTrailOffset(arity)].Data;
            parentExtraTop = (int)_stack[_b + CpExtraTrailOffset(arity)].Data;
            parentHeapTop = (int)_stack[_b + CpHeapTopOffset(arity)].Data;
        }

        CompactTrails(parentBindingTop, parentExtraTop, parentHeapTop);
    }

    /// <summary>Copies the current <see cref="B"/> into <c>Y[slot]</c> of the current
    /// environment frame. Used by the WAM <c>get_level</c> instruction to capture the
    /// cut barrier in a permanent variable.</summary>
    public void GetLevel(int slot) => SetY(slot, new Cell(_b));

    /// <summary>
    /// Cut back to <see cref="B0"/> — the value of <c>B</c> recorded at the most recent
    /// procedure entry. This is the implicit barrier used by the WAM <c>neck_cut</c>
    /// instruction. The interpreter's <c>call</c> and <c>execute</c> opcodes maintain
    /// <c>B0</c> by writing <c>_b</c> into it before transferring control to the callee.
    /// </summary>
    public void NeckCut() => Cut(_b0);

    /// <summary>
    /// Single-pass interleaved trail compaction (Warren's algorithm extended to the extra
    /// trail). Both trails are walked in temporal order: for each surviving extra entry,
    /// its <see cref="ExtraTrailEntry.BindingTrailMarker"/> is rewritten to the index it
    /// would occupy in the compacted binding trail, preserving the relative ordering that
    /// <see cref="UnwindTrails"/> relies on.
    /// </summary>
    private void CompactTrails(int parentBindingTop, int parentExtraTop, int parentHeapTop)
    {
        int bindingRead = parentBindingTop;
        int bindingWrite = parentBindingTop;
        int extraRead = parentExtraTop;
        int extraWrite = parentExtraTop;

        while (extraRead < _extraTrailTop)
        {
            var entry = _extraTrail[extraRead];
            int marker = entry.BindingTrailMarker;

            // Compact all binding entries that came before this extra entry.
            int stop = marker < _bindingTrailTop ? marker : _bindingTrailTop;
            while (bindingRead < stop)
            {
                int idx = _bindingTrail[bindingRead];
                if (idx < parentHeapTop)
                    _bindingTrail[bindingWrite++] = idx;
                bindingRead++;
            }

            if (entry.HeapIdx < parentHeapTop)
            {
                entry.BindingTrailMarker = bindingWrite;
                _extraTrail[extraWrite++] = entry;
            }
            extraRead++;
        }

        // Any binding entries left after the last extra entry.
        while (bindingRead < _bindingTrailTop)
        {
            int idx = _bindingTrail[bindingRead];
            if (idx < parentHeapTop)
                _bindingTrail[bindingWrite++] = idx;
            bindingRead++;
        }

        _bindingTrailTop = bindingWrite;
        _extraTrailTop = extraWrite;
    }

    // ----- Registers -----

    public Cell GetRegister(int idx) => _registers[idx];
    public void SetRegister(int idx, Cell value) => _registers[idx] = value;

    // ----- Unify against registers / permanents -----
    //
    // The base Unify(int, int) operates on heap addresses. Register and permanent
    // (Y-slot) cells live outside the heap, so the helpers below "materialise" them:
    // if the cell already holds a REF we reuse its heap target directly; otherwise we
    // copy the atomic value into a freshly-allocated heap cell. This costs at most one
    // extra heap cell per atomic operand and keeps the unify implementation single-form.

    /// <summary>Unifies the cells held in <c>X[<paramref name="aRegIdx"/>]</c> and
    /// <c>X[<paramref name="bRegIdx"/>]</c>.</summary>
    public bool UnifyRegisters(int aRegIdx, int bRegIdx)
    {
        int aHeap = MaterializeRegister(aRegIdx);
        int bHeap = MaterializeRegister(bRegIdx);
        return Unify(aHeap, bHeap);
    }

    /// <summary>Unifies the cell held in <c>X[<paramref name="regIdx"/>]</c> with an
    /// immediate <paramref name="value"/> (typically a bytecode-literal cell such as
    /// <see cref="Cell.Atom"/> or <see cref="Cell.Int"/>).</summary>
    public bool UnifyRegisterWithCell(int regIdx, Cell value)
    {
        int regHeap = MaterializeRegister(regIdx);
        int valueHeap = AllocateHeap(1);
        _heap[valueHeap] = value;
        return Unify(regHeap, valueHeap);
    }

    /// <summary>Unifies the cell held in <c>Y[<paramref name="permSlot"/>]</c> of the
    /// current environment frame with <c>X[<paramref name="regIdx"/>]</c>.</summary>
    public bool UnifyPermanentWithRegister(int permSlot, int regIdx)
    {
        if (_e < 0)
            throw new InvalidOperationException("No environment frame is active.");
        int permHeap = MaterializePermanent(permSlot);
        int regHeap = MaterializeRegister(regIdx);
        return Unify(permHeap, regHeap);
    }

    private int MaterializeRegister(int regIdx)
    {
        Cell c = _registers[regIdx];
        if (c.Tag == Tag.Ref) return c.AsHeapIndex;
        int slot = AllocateHeap(1);
        _heap[slot] = c;
        return slot;
    }

    private int MaterializePermanent(int permSlot)
    {
        int stackIdx = _e + EnvY1Offset + permSlot;
        Cell c = _stack[stackIdx];
        if (c.Tag == Tag.Ref) return c.AsHeapIndex;
        int slot = AllocateHeap(1);
        _heap[slot] = c;
        return slot;
    }

    // ----- Auxiliary value tables -----

    /// <summary>
    /// Stores <paramref name="value"/> in the engine's BigInteger table and returns a
    /// BIGINT cell whose payload is its id. The cell is meaningful only for this engine
    /// (auxiliary tables are not shared, unlike atoms and functors).
    /// </summary>
    public Cell MakeBigInt(BigInteger value)
    {
        int id = _bigIntTable.Count;
        _bigIntTable.Add(value);
        return Cell.BigInt(id);
    }

    /// <summary>Returns the <see cref="BigInteger"/> referenced by a BIGINT cell.</summary>
    public BigInteger AsBigInt(Cell cell)
    {
        if (cell.Tag != Tag.BigInt)
            throw new InvalidOperationException($"Cell tag is {cell.Tag}, expected BigInt.");
        return _bigIntTable[cell.AsBigIntId];
    }

    /// <summary>Stores <paramref name="value"/> in the engine's string table and returns
    /// a STRING cell whose payload is its id.</summary>
    public Cell MakeString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int id = _stringTable.Count;
        _stringTable.Add(value);
        return Cell.String(id);
    }

    /// <summary>Returns the string referenced by a STRING cell.</summary>
    public string AsString(Cell cell)
    {
        if (cell.Tag != Tag.String)
            throw new InvalidOperationException($"Cell tag is {cell.Tag}, expected String.");
        return _stringTable[cell.AsStringId];
    }

    /// <summary>Stores <paramref name="value"/> in the engine's foreign-object table and
    /// returns a FOREIGN cell whose payload is its id. The value may be <c>null</c>.</summary>
    public Cell MakeForeign(object? value)
    {
        int id = _foreignTable.Count;
        _foreignTable.Add(value);
        return Cell.Foreign(id);
    }

    /// <summary>Returns the object referenced by a FOREIGN cell (possibly <c>null</c>).</summary>
    public object? AsForeign(Cell cell)
    {
        if (cell.Tag != Tag.Foreign)
            throw new InvalidOperationException($"Cell tag is {cell.Tag}, expected Foreign.");
        return _foreignTable[cell.AsForeignId];
    }

    /// <summary>Typed accessor over <see cref="AsForeign(Cell)"/>. Casts to <typeparamref name="T"/>;
    /// returns <c>default</c> if the stored value is <c>null</c>.</summary>
    public T? AsForeign<T>(Cell cell) where T : class
    {
        object? value = AsForeign(cell);
        if (value is null) return null;
        if (value is T typed) return typed;
        throw new InvalidCastException($"Foreign value of type {value.GetType()} is not assignable to {typeof(T)}.");
    }

    /// <summary>
    /// Allocates a FLOAT header cell and its paired INT cell contiguously on the heap
    /// and returns the heap index of the header. Together they encode the 64-bit double
    /// per the two-cell layout in <see cref="Cell.MakeFloat(double, int)"/>.
    /// </summary>
    public int MakeFloat(double value)
    {
        int headerIdx = AllocateHeap(2);
        var (header, paired) = Cell.MakeFloat(value, headerIdx + 1);
        _heap[headerIdx] = header;
        _heap[headerIdx + 1] = paired;
        return headerIdx;
    }

    /// <summary>
    /// Allocates a PSTR header, the necessary buffer cells (each holding three UTF-16
    /// code units), and a tail cell initialised to <c>[]</c>, contiguously on the heap.
    /// Returns the heap index of the header. Total cells used: <c>1 + ceil(len/3) + 1</c>.
    /// </summary>
    public int MakePstr(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int codeUnits = value.Length;
        int bufferCellCount = (codeUnits + Cell.PstrCodeUnitsPerBuffer - 1) / Cell.PstrCodeUnitsPerBuffer;
        int totalCells = 1 + bufferCellCount + 1;

        int headerIdx = AllocateHeap(totalCells);
        int bufferStart = headerIdx + 1;
        int tailIdx = bufferStart + bufferCellCount;

        for (int i = 0; i < bufferCellCount; i++)
        {
            int basePos = i * Cell.PstrCodeUnitsPerBuffer;
            int cu0 = basePos < codeUnits ? value[basePos] : 0;
            int cu1 = (basePos + 1) < codeUnits ? value[basePos + 1] : 0;
            int cu2 = (basePos + 2) < codeUnits ? value[basePos + 2] : 0;
            _heap[bufferStart + i] = Cell.PstrBuffer(cu0, cu1, cu2);
        }

        _heap[tailIdx] = Cell.Atom(AtomTable.EmptyListId);
        _heap[headerIdx] = Cell.Pstr(codeUnits, bufferStart, 0);
        return headerIdx;
    }

    /// <summary>
    /// Reconstructs the .NET string represented by the PSTR header at <paramref name="headerIdx"/>.
    /// Reads only the <c>length</c> code units starting from the encoded offset.
    /// </summary>
    public string AsPstrString(int headerIdx)
    {
        Cell header = _heap[headerIdx];
        if (header.Tag != Tag.Pstr)
            throw new InvalidOperationException($"Cell tag is {header.Tag}, expected Pstr.");
        int length = header.AsPstrLength;
        if (length == 0) return "";
        var sb = new System.Text.StringBuilder(length);
        for (int i = 0; i < length; i++)
            sb.Append((char)GetPstrCodeUnit(header, i));
        return sb.ToString();
    }

    /// <summary>Heap index of the cell that immediately follows a PSTR's buffer cells.
    /// That cell is the tail value (typically <c>[]</c>, a variable, another PSTR, or
    /// a LIS in the "fallback to cons" case).</summary>
    public int GetPstrTailIndex(int headerIdx)
    {
        Cell header = _heap[headerIdx];
        if (header.Tag != Tag.Pstr)
            throw new InvalidOperationException($"Cell tag is {header.Tag}, expected Pstr.");
        return ComputePstrTailIndex(header);
    }

    private int GetPstrCodeUnit(Cell header, int i)
    {
        int absolute = header.AsPstrOffset + i;
        int cellIdx = header.AsPstrBufferIndex + absolute / Cell.PstrCodeUnitsPerBuffer;
        int posInCell = absolute % Cell.PstrCodeUnitsPerBuffer;
        return _heap[cellIdx].AsPstrCodeUnit(posInCell);
    }

    private static int ComputePstrTailIndex(Cell header)
    {
        int length = header.AsPstrLength;
        int offset = header.AsPstrOffset;
        int bufferIdx = header.AsPstrBufferIndex;
        int bufferCellCount = (offset + length + Cell.PstrCodeUnitsPerBuffer - 1) / Cell.PstrCodeUnitsPerBuffer;
        return bufferIdx + bufferCellCount;
    }

    internal int BigIntTableCount => _bigIntTable.Count;
    internal int StringTableCount => _stringTable.Count;
    internal int ForeignTableCount => _foreignTable.Count;

    // ----- Trail accessors -----

    public int BindingTrailTop => _bindingTrailTop;
    public int BindingTrailCapacity => _bindingTrail.Length;
    public int ExtraTrailTop => _extraTrailTop;
    public int ExtraTrailCapacity => _extraTrail.Length;

    // ----- Engine register accessors (read-only for now; set by the interpreter later) -----

    public int E => _e;
    public int B => _b;
    public int B0 => _b0;
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
    /// Use this only when no <see cref="ExtraTrailEntry"/> entries are involved — otherwise
    /// call <see cref="UnwindTrails"/> for the interleaved version required by ADR-004.
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

    /// <summary>
    /// Records a value-change entry on the extra trail. Use this when overwriting an
    /// already-bound cell whose previous value must survive backtracking. The
    /// <see cref="ExtraTrailEntry.BindingTrailMarker"/> captures the current binding-trail
    /// depth so <see cref="UnwindTrails"/> can interleave correctly with binding unwind.
    /// </summary>
    public void TrailValueChange(int heapIdx, Cell oldValue)
    {
        EnsureExtraTrailCapacity(1);
        _extraTrail[_extraTrailTop++] = new ExtraTrailEntry
        {
            Type = TrailType.ValueChange,
            HeapIdx = heapIdx,
            OldValue = oldValue,
            BindingTrailMarker = _bindingTrailTop,
        };
    }

    /// <summary>
    /// Interleaved unwind of both trails back to the given target depths. Walks the extra
    /// trail in reverse; for each entry, first rolls back binding-trail entries down to
    /// the entry's <see cref="ExtraTrailEntry.BindingTrailMarker"/>, then applies the
    /// extra entry itself. Once the extra trail reaches <paramref name="extraTarget"/>,
    /// any remaining binding-trail entries above <paramref name="bindingTarget"/> are
    /// rolled back in a final pass.
    /// </summary>
    public void UnwindTrails(int bindingTarget, int extraTarget)
    {
        if (bindingTarget < 0 || bindingTarget > _bindingTrailTop)
            throw new ArgumentOutOfRangeException(nameof(bindingTarget));
        if (extraTarget < 0 || extraTarget > _extraTrailTop)
            throw new ArgumentOutOfRangeException(nameof(extraTarget));

        while (_extraTrailTop > extraTarget)
        {
            ref var entry = ref _extraTrail[_extraTrailTop - 1];
            while (_bindingTrailTop > entry.BindingTrailMarker)
            {
                int idx = _bindingTrail[--_bindingTrailTop];
                _heap[idx] = Cell.UnboundVar(idx);
            }
            ProcessExtraUnwind(entry);
            _extraTrailTop--;
        }
        while (_bindingTrailTop > bindingTarget)
        {
            int idx = _bindingTrail[--_bindingTrailTop];
            _heap[idx] = Cell.UnboundVar(idx);
        }
    }

    private void ProcessExtraUnwind(in ExtraTrailEntry entry)
    {
        switch (entry.Type)
        {
            case TrailType.ValueChange:
                _heap[entry.HeapIdx] = entry.OldValue;
                break;
            default:
                throw new InvalidOperationException(
                    $"Unwind not yet implemented for TrailType.{entry.Type}.");
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

        // PSTR participates in cross-tag unification (with LIS and the [] atom in particular),
        // so it must be dispatched before the strict tag-equality check.
        if (aCell.Tag == Tag.Pstr) return UnifyPstr(aAddr, bAddr);
        if (bCell.Tag == Tag.Pstr) return UnifyPstr(bAddr, aAddr);

        // Both bound, neither REF, neither PSTR.
        if (aCell.Tag != bCell.Tag) return false;
        return aCell.Tag switch
        {
            Tag.Atom => aCell.AsAtomId == bCell.AsAtomId,
            Tag.Int => aCell.AsInt == bCell.AsInt,
            Tag.Str => UnifyStr(aCell.AsHeapIndex, bCell.AsHeapIndex),
            Tag.Lis => UnifyLis(aCell.AsHeapIndex, bCell.AsHeapIndex),
            Tag.BigInt => _bigIntTable[aCell.AsBigIntId].Equals(_bigIntTable[bCell.AsBigIntId]),
            Tag.String => string.Equals(_stringTable[aCell.AsStringId], _stringTable[bCell.AsStringId]),
            Tag.Foreign => ReferenceEquals(_foreignTable[aCell.AsForeignId], _foreignTable[bCell.AsForeignId]),
            Tag.Float => UnifyFloat(aCell, bCell),
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

    /// <summary>
    /// Unifies two FLOAT-tagged cells by reconstructing the double from each header and
    /// its paired INT cell and comparing the raw bit patterns. Bit-exact comparison gives
    /// NaN unifying with NaN (consistent with SWI-Prolog's <c>=/2</c>) and keeps +0.0 and
    /// -0.0 distinct (their bits differ even though <c>==</c> returns true).
    /// </summary>
    private bool UnifyFloat(Cell aHeader, Cell bHeader)
    {
        double a = Cell.DecodeFloat(aHeader, _heap[aHeader.FloatPairedIndex]);
        double b = Cell.DecodeFloat(bHeader, _heap[bHeader.FloatPairedIndex]);
        return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    }

    /// <summary>
    /// Unifies a PSTR (at <paramref name="pstrAddr"/>) with any other dereferenced cell.
    /// An empty PSTR is logically just its tail value; non-empty PSTRs can only match a
    /// LIS (character-by-character decomposition) or another PSTR. Anything else fails —
    /// including the empty-list atom against a non-empty PSTR.
    /// </summary>
    private bool UnifyPstr(int pstrAddr, int otherAddr)
    {
        Cell pstrHdr = _heap[pstrAddr];
        int length = pstrHdr.AsPstrLength;

        if (length == 0)
        {
            int tailIdx = ComputePstrTailIndex(pstrHdr);
            return Unify(tailIdx, otherAddr);
        }

        Cell otherCell = _heap[otherAddr];
        return otherCell.Tag switch
        {
            Tag.Pstr => UnifyPstrPstr(pstrAddr, otherAddr),
            Tag.Lis => UnifyPstrLis(pstrAddr, otherAddr),
            _ => false,
        };
    }

    /// <summary>
    /// Unifies two PSTRs. Compares the first <c>min(aLen, bLen)</c> code units pairwise;
    /// on equal length, the stored tail values are unified directly. On a length mismatch,
    /// the longer PSTR is sliced from the shorter's length onward (a virtual header is
    /// materialised on the heap pointing at the same buffer) and that slice is unified
    /// with the shorter PSTR's tail.
    /// </summary>
    private bool UnifyPstrPstr(int aAddr, int bAddr)
    {
        Cell aHdr = _heap[aAddr];
        Cell bHdr = _heap[bAddr];
        int aLen = aHdr.AsPstrLength;
        int bLen = bHdr.AsPstrLength;

        if (aLen > bLen)
        {
            (aAddr, bAddr) = (bAddr, aAddr);
            (aHdr, bHdr) = (bHdr, aHdr);
            (aLen, bLen) = (bLen, aLen);
        }

        for (int i = 0; i < aLen; i++)
        {
            if (GetPstrCodeUnit(aHdr, i) != GetPstrCodeUnit(bHdr, i))
                return false;
        }

        int aTailIdx = ComputePstrTailIndex(aHdr);

        if (aLen == bLen)
        {
            int bTailIdx = ComputePstrTailIndex(bHdr);
            return Unify(aTailIdx, bTailIdx);
        }

        // Build a virtual slice of B starting at position aLen. The slice points at the
        // same buffer (no copy) and shares B's tail position by layout.
        int absoluteStart = bHdr.AsPstrOffset + aLen;
        int sliceBufferIdx = bHdr.AsPstrBufferIndex + absoluteStart / Cell.PstrCodeUnitsPerBuffer;
        int sliceOffset = absoluteStart % Cell.PstrCodeUnitsPerBuffer;
        int sliceLength = bLen - aLen;

        int sliceSlot = AllocateHeap(1);
        _heap[sliceSlot] = Cell.Pstr(sliceLength, sliceBufferIdx, sliceOffset);
        return Unify(aTailIdx, sliceSlot);
    }

    /// <summary>
    /// Unifies a non-empty PSTR with a cons cell. Decomposes the first code unit as
    /// <c>Int(cu)</c>, then recurses on the LIS tail with either the PSTR's stored tail
    /// (length 1) or a virtual one-shorter PSTR slice.
    ///
    /// <para>Heads are emitted as 16-bit UTF-16 code units; supplementary codepoints
    /// (above U+FFFF) appear as two separate surrogate values rather than one combined
    /// codepoint. This is enough for the BMP-only grammar workloads that motivate Phase 1;
    /// surrogate-pair fusion is a future refinement.</para>
    /// </summary>
    private bool UnifyPstrLis(int pstrAddr, int lisAddr)
    {
        Cell pstrHdr = _heap[pstrAddr];
        int length = pstrHdr.AsPstrLength;
        int firstUnit = GetPstrCodeUnit(pstrHdr, 0);

        int lisHeadIdx = _heap[lisAddr].AsHeapIndex;
        int lisTailIdx = lisHeadIdx + 1;

        // Unify the head: a fresh Int cell against the LIS's head slot.
        int headSlot = AllocateHeap(1);
        _heap[headSlot] = Cell.Int(firstUnit);
        if (!Unify(headSlot, lisHeadIdx)) return false;

        if (length == 1)
        {
            int origTailIdx = ComputePstrTailIndex(pstrHdr);
            return Unify(origTailIdx, lisTailIdx);
        }

        int absoluteStart = pstrHdr.AsPstrOffset + 1;
        int newBufferIdx = pstrHdr.AsPstrBufferIndex + absoluteStart / Cell.PstrCodeUnitsPerBuffer;
        int newOffset = absoluteStart % Cell.PstrCodeUnitsPerBuffer;

        int sliceSlot = AllocateHeap(1);
        _heap[sliceSlot] = Cell.Pstr(length - 1, newBufferIdx, newOffset);
        return Unify(sliceSlot, lisTailIdx);
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

    private void EnsureStackCapacity(int extra)
        => GrowIfNeeded(ref _stack, _stackTop, extra, _config.MaxStackSize, "stack");

    private void EnsureBindingTrailCapacity(int extra)
        => GrowIfNeeded(ref _bindingTrail, _bindingTrailTop, extra, _config.MaxBindingTrailSize, "binding trail");

    private void EnsureExtraTrailCapacity(int extra)
        => GrowIfNeeded(ref _extraTrail, _extraTrailTop, extra, _config.MaxExtraTrailSize, "extra trail");

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

    /// <summary>Sets <c>CP</c> directly. The interpreter uses this from the <c>call</c>
    /// instruction; tests use it to seed the engine state before running a fragment.</summary>
    internal void SetCp(int cp) => _cp = cp;

    /// <summary>Sets <c>PC</c> directly. Used by the interpreter for jumps
    /// (<c>execute</c>, <c>proceed</c>) and by Run for the initial entry point.</summary>
    internal void SetPc(int pc) => _p = pc;

    /// <summary>Advances <c>PC</c> by <paramref name="delta"/> bytes. Used by the
    /// interpreter to step past straight-line instructions.</summary>
    internal void AdvancePc(int delta) => _p += delta;

    /// <summary>Sets <c>B0</c> directly. The interpreter writes <c>_b</c> into this
    /// before any <c>call</c> or <c>execute</c> so the callee's <c>neck_cut</c> sees
    /// the right barrier.</summary>
    internal void SetB0(int b0) => _b0 = b0;

    internal ReadOnlySpan<int> BindingTrailSpan => _bindingTrail.AsSpan(0, _bindingTrailTop);
}
