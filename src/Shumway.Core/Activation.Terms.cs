using System.Numerics;

namespace Shumway.Core;

public sealed partial class Activation
{
    // ----- Auxiliary value tables -----

    /// <summary>
    /// Stores <paramref name="value"/> in the engine's BigInteger table and returns a
    /// BIGINT cell whose payload is its id. The cell is meaningful only for this engine
    /// (auxiliary tables are not shared, unlike atoms and functors).
    ///
    /// <para>Values that fit in the 60-bit inline range collapse to <see cref="Cell.Int"/>
    /// instead of consuming a side-table slot. The runtime invariant is that a value
    /// in 60-bit range is always represented as <c>Tag.Int</c>, never <c>Tag.BigInt</c>;
    /// keeping the canonical form unique lets unification compare values by raw cell
    /// equality without crossing tag boundaries.</para>
    /// </summary>
    public Cell MakeBigInt(BigInteger value)
    {
        if (value >= Cell.MinInt60 && value <= Cell.MaxInt60)
            return Cell.Int((long)value);
        int id = _bigIntTable.Count;
        _bigIntTable.Add(value);
        // Record the allocation on the extra trail so a later backtrack
        // truncates the table back to its pre-allocation size and frees
        // the slot (BigInt trail-aware allocation).
        TrailBigIntAlloc(id);
        return Cell.BigInt(id);
    }

    private void TrailBigIntAlloc(int oldCount)
    {
        EnsureExtraTrailCapacity(1);
        _extraTrail[_extraTrailTop++] = new ExtraTrailEntry
        {
            Type = TrailType.BigIntAlloc,
            HeapIdx = oldCount,
            OldValue = default,
            BindingTrailMarker = _bindingTrailTop,
        };
    }

    /// <summary>Returns the <see cref="BigInteger"/> referenced by a BIGINT cell.</summary>
    public BigInteger AsBigInt(Cell cell)
    {
        if (cell.Tag != Tag.BigInt)
            throw new InvalidOperationException($"Cell tag is {cell.Tag}, expected BigInt.");
        return _bigIntTable[cell.AsBigIntId];
    }

    /// <summary>
    /// Stores an exact rational and returns a cell for it (ADR-039). An integral
    /// value (reduced denominator 1) collapses to an integer cell — every
    /// <see cref="Tag.Rational"/> cell is a genuine fraction, so unification /
    /// equality never has to reconcile two representations of the same value.
    /// The side-table slot is trailed like a BigInteger, freed on backtrack.
    /// </summary>
    public Cell MakeRational(Rational value)
    {
        if (value.IsInteger) return MakeBigInt(value.Num);
        int id = _rationalTable.Count;
        _rationalTable.Add(value);
        EnsureExtraTrailCapacity(1);
        _extraTrail[_extraTrailTop++] = new ExtraTrailEntry
        {
            Type = TrailType.RationalAlloc,
            HeapIdx = id,
            OldValue = default,
            BindingTrailMarker = _bindingTrailTop,
        };
        return Cell.Rational(id);
    }

    /// <summary>Convenience: build a rational from numerator/denominator
    /// (reduces + normalises sign; collapses to integer when integral).</summary>
    public Cell MakeRational(BigInteger num, BigInteger den)
        => MakeRational(Rational.Create(num, den));

    // Backtrackable external state (b_setval / Scryer bb_b_put): each trailed
    // write logs (target, key, old value) here; the ExtraTrail entry's HeapIdx
    // indexes this log, and unwinding invokes the target's restore. LIFO with
    // the extra trail, so truncation at the entry's index is exact.
    private List<(IExternalTrailTarget Target, int Key, Cell Old, bool Had)>? _externalTrailLog;

    /// <summary>Records a backtrackable external-state write: unwinding past
    /// this point calls <paramref name="target"/>.RestoreExternal with the
    /// captured previous value. The old value cell participates in heap GC
    /// (marked + relocated) so a compound previous value survives a mid-query
    /// collection.</summary>
    public void TrailExternal(IExternalTrailTarget target, int key, Cell oldValue, bool hadOldValue)
    {
        _externalTrailLog ??= new List<(IExternalTrailTarget, int, Cell, bool)>();
        int idx = _externalTrailLog.Count;
        _externalTrailLog.Add((target, key, oldValue, hadOldValue));
        EnsureExtraTrailCapacity(1);
        _extraTrail[_extraTrailTop++] = new ExtraTrailEntry
        {
            Type = TrailType.MutableSet,
            HeapIdx = idx,
            OldValue = default,
            BindingTrailMarker = _bindingTrailTop,
        };
    }

    // Heap-GC participation for the external trail log's old-value cells.
    internal void MarkExternalTrailRoots(Action<Cell> markReferents)
    {
        if (_externalTrailLog is null) return;
        foreach (var (_, _, old, had) in _externalTrailLog)
            if (had) markReferents(old);
    }

    internal void RelocateExternalTrail(Func<Cell, Cell> reloc)
    {
        if (_externalTrailLog is null) return;
        for (int i = 0; i < _externalTrailLog.Count; i++)
        {
            var e = _externalTrailLog[i];
            if (e.Had) _externalTrailLog[i] = (e.Target, e.Key, reloc(e.Old), e.Had);
        }
    }

    /// <summary>Returns the <see cref="Rational"/> referenced by a RATIONAL cell.</summary>
    public Rational AsRational(Cell cell)
    {
        if (cell.Tag != Tag.Rational)
            throw new InvalidOperationException($"Cell tag is {cell.Tag}, expected Rational.");
        return _rationalTable[cell.AsRationalId];
    }

    internal int RationalTableCount => _rationalTable.Count;

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

    /// <summary>The foreign-table entry by raw id, or null when out of range. The
    /// debugger's attvar transplant reads a SUSPENDED activation's table with this to
    /// re-register the object on the evaluation activation (foreign ids are
    /// per-activation).</summary>
    public object? ForeignById(int id)
        => id >= 0 && id < _foreignTable.Count ? _foreignTable[id] : null;

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
    /// <summary>Like <see cref="MakePstr"/> but leaves the tail cell an UNBOUND
    /// variable instead of <c>[]</c>, so the result is a PARTIAL list. Returns
    /// the header index; the tail's own index is
    /// <see cref="GetPstrTailIndex"/> of it.
    ///
    /// <para>This is what lazy stream reading is built on: a window of text
    /// whose tail is a frozen variable, so a DCG walking it pulls the next
    /// window only when it reaches the end of this one. The design always
    /// admitted the shape — a PSTR tail may be a <c>Ref</c> — and this is the
    /// constructor for it.</para></summary>
    public int MakePstrOpen(string value, TextKind kind)
    {
        int headerIdx = MakePstr(value, kind);
        int tailIdx = ComputePstrTailIndex(_heap[headerIdx]);
        _heap[tailIdx] = Cell.Ref(tailIdx);
        return headerIdx;
    }

    /// <summary>Builds the list of characters or codes named by
    /// <paramref name="kind"/>, PACKED (ADR-047 decision 8) — the single entry
    /// point every runtime text producer goes through. Returns a heap index
    /// whose cell is the list: the atom <c>[]</c> when the text is empty, a
    /// PSTR header otherwise.
    ///
    /// <para>This is where the arc's measurement lands: <c>atom_codes/2</c> of a
    /// 4000-character atom used to write 8002 cells and now writes 1337. Six
    /// near-identical cons builders across four files collapsed into it.</para></summary>
    public int MakeTextList(string text, TextKind kind)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length != 0) return MakePstr(text, kind);
        int nil = AllocateHeap(1);
        _heap[nil] = Cell.Atom(AtomTable.EmptyListId);
        return nil;
    }

    public int MakePstr(string value, TextKind kind)
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
        _heap[headerIdx] = Cell.Pstr(codeUnits, bufferStart, 0, kind);
        return headerIdx;
    }

    /// <summary>
    /// Reconstructs the .NET string represented by the PSTR header at <paramref name="headerIdx"/>.
    /// Reads each segment's code units then follows the tail cell — the lazy concat
    /// chains multiple PSTR pieces together by storing a <see cref="Tag.Pstr"/> cell in the
    /// tail position, and this walker treats such tails as continuation segments.
    /// </summary>
    public string AsPstrString(int headerIdx)
    {
        Cell header = _heap[headerIdx];
        if (header.Tag != Tag.Pstr)
            throw new InvalidOperationException($"Cell tag is {header.Tag}, expected Pstr.");
        var sb = new System.Text.StringBuilder(header.AsPstrLength);
        AppendPstrChain(sb, header);
        return sb.ToString();
    }

    /// <summary>Reads a PSTR chain's text starting
    /// at the given (already dereferenced) <see cref="Tag.Pstr"/> cell
    /// and returns the final non-PSTR tail cell (dereferenced), so
    /// list-walking builtins (<c>atom_codes/2</c>, <c>number_codes/2</c>,
    /// …) can consume a partial string as the code list it represents —
    /// including a PSTR sitting in the tail of an ordinary cons chain,
    /// e.g. <c>[0'a | "bc"]</c>.</summary>
    public string ReadPstrChain(Cell header, out Cell tail)
    {
        var sb = new System.Text.StringBuilder(header.AsPstrLength);
        TextKind kind = header.AsPstrKind;
        while (header.Tag == Tag.Pstr && header.AsPstrKind == kind)
        {
            int length = header.AsPstrLength;
            for (int i = 0; i < length; i++)
                sb.Append((char)GetPstrCodeUnit(header, i));
            Cell t = _heap[ComputePstrTailIndex(header)];
            if (t.Tag == Tag.Ref)
                t = _heap[Deref(t.AsHeapIndex)];
            header = t;
        }
        tail = header;
        return sb.ToString();
    }

    private void AppendPstrChain(System.Text.StringBuilder sb, Cell header)
    {
        TextKind kind = header.AsPstrKind;
        while (header.Tag == Tag.Pstr && header.AsPstrKind == kind)
        {
            int length = header.AsPstrLength;
            for (int i = 0; i < length; i++)
                sb.Append((char)GetPstrCodeUnit(header, i));
            int tailIdx = ComputePstrTailIndex(header);
            Cell tail = _heap[tailIdx];
            if (tail.Tag == Tag.Ref)
            {
                int derefIdx = Deref(tail.AsHeapIndex);
                tail = _heap[derefIdx];
            }
            header = tail;
        }
    }

    /// <summary>Total logical length of a PSTR chain in UTF-16 code units —
    /// follows tail cells when they are <see cref="Tag.Pstr"/> (the
    /// lazy concat representation). Returns the immediate segment's length
    /// when the tail is anything else.</summary>
    public int GetPstrChainLength(int headerIdx)
    {
        int total = 0;
        Cell header = _heap[headerIdx];
        TextKind kind = header.AsPstrKind;
        while (header.Tag == Tag.Pstr && header.AsPstrKind == kind)
        {
            total += header.AsPstrLength;
            int tailIdx = ComputePstrTailIndex(header);
            Cell tail = _heap[tailIdx];
            if (tail.Tag == Tag.Ref)
            {
                int derefIdx = Deref(tail.AsHeapIndex);
                tail = _heap[derefIdx];
            }
            header = tail;
        }
        return total;
    }

    /// <summary>Lazy <c>pstr_concat</c>: builds a new PSTR whose
    /// buffer holds <paramref name="aIdx"/>'s logical content (flattening
    /// any pre-existing tail chain) and whose tail cell stores a
    /// <see cref="Tag.Pstr"/> reference to <paramref name="bIdx"/>'s
    /// header. The result's right side (B) is shared without copying;
    /// only the left side is materialised into a fresh buffer. For
    /// concat-then-decompose grammar pipelines this avoids the
    /// <c>O(N_b)</c> cell allocation per join that the eager
    /// <c>MakePstr(aStr + bStr)</c> path used to pay.
    ///
    /// <para>Special cases: when A or B is logically empty, the result
    /// is just the non-empty side (no allocation). When B is empty but
    /// non-PSTR (e.g. the <c>[]</c> atom — caller's responsibility to
    /// recognise that case), the caller should fall back to the eager
    /// path.</para></summary>
    public int MakePstrConcat(int aIdx, int bIdx)
    {
        Cell aHdr = _heap[aIdx];
        Cell bHdr = _heap[bIdx];
        if (aHdr.Tag != Tag.Pstr)
            throw new InvalidOperationException(
                $"MakePstrConcat: A's cell tag is {aHdr.Tag}, expected Pstr.");
        if (bHdr.Tag != Tag.Pstr)
            throw new InvalidOperationException(
                $"MakePstrConcat: B's cell tag is {bHdr.Tag}, expected Pstr.");
        // Chaining a chars segment onto a codes one would build the mixed list
        // [a,b,c,97,98] behind a single header, and every chain walker would
        // have to stop mid-buffer. Callers concatenate same-kind text or fall
        // back to the cons path.
        if (aHdr.AsPstrKind != bHdr.AsPstrKind)
            throw new InvalidOperationException(
                $"MakePstrConcat: A is {aHdr.AsPstrKind}, B is {bHdr.AsPstrKind}.");

        int totalALength = GetPstrChainLength(aIdx);
        int bChainLength = GetPstrChainLength(bIdx);
        if (totalALength == 0) return bIdx;
        if (bChainLength == 0) return aIdx;

        int bufferCellCount =
            (totalALength + Cell.PstrCodeUnitsPerBuffer - 1) / Cell.PstrCodeUnitsPerBuffer;
        int totalCells = 1 + bufferCellCount + 1;
        int headerIdx = AllocateHeap(totalCells);
        int bufferStart = headerIdx + 1;
        int tailIdx = bufferStart + bufferCellCount;

        // Copy A's full content (following any existing chain) into the
        // new buffer in 3-code-unit groups.
        var aChars = new char[totalALength];
        FillCharsFromPstrChain(_heap[aIdx], aChars);
        for (int i = 0; i < bufferCellCount; i++)
        {
            int basePos = i * Cell.PstrCodeUnitsPerBuffer;
            int cu0 = basePos < totalALength ? aChars[basePos] : 0;
            int cu1 = (basePos + 1) < totalALength ? aChars[basePos + 1] : 0;
            int cu2 = (basePos + 2) < totalALength ? aChars[basePos + 2] : 0;
            _heap[bufferStart + i] = Cell.PstrBuffer(cu0, cu1, cu2);
        }

        // Tail of the new PSTR is B's header value — by storing a Pstr
        // cell here we extend the chain without copying B's buffer. The
        // unification path's recursive Unify on tail indices follows the
        // chain transparently (UnifyPstrPstr dispatches on Pstr-Pstr).
        _heap[tailIdx] = _heap[bIdx];
        _heap[headerIdx] = Cell.Pstr(totalALength, bufferStart, 0, aHdr.AsPstrKind);
        return headerIdx;
    }

    private void FillCharsFromPstrChain(Cell header, char[] dst)
    {
        int writeIdx = 0;
        TextKind kind = header.AsPstrKind;
        while (header.Tag == Tag.Pstr && header.AsPstrKind == kind)
        {
            int length = header.AsPstrLength;
            for (int i = 0; i < length; i++)
                dst[writeIdx++] = (char)GetPstrCodeUnit(header, i);
            int tailIdx = ComputePstrTailIndex(header);
            Cell tail = _heap[tailIdx];
            if (tail.Tag == Tag.Ref)
            {
                int derefIdx = Deref(tail.AsHeapIndex);
                tail = _heap[derefIdx];
            }
            header = tail;
        }
    }

    /// <summary>Heap index of the cell that immediately follows a PSTR's buffer cells.
    /// That cell is the tail value (typically <c>[]</c>, a variable, another PSTR, or
    /// a LIS in the "fallback to cons" case).</summary>
    /// <summary>Tail-cell index of a PSTR given the header CELL rather than its
    /// heap address — a slice arrives as a computed value with no address of
    /// its own.</summary>
    public int GetPstrTailIndexOf(Cell header) => ComputePstrTailIndex(header);

    public int GetPstrTailIndex(int headerIdx)
    {
        Cell header = _heap[headerIdx];
        if (header.Tag != Tag.Pstr)
            throw new InvalidOperationException($"Cell tag is {header.Tag}, expected Pstr.");
        return ComputePstrTailIndex(header);
    }

    /// <summary>
    /// Reads the <paramref name="i"/>-th code unit (0-indexed within the PSTR's logical
    /// content) from the PSTR header at <paramref name="headerIdx"/>. Public surface for
    /// the interpreter's PSTR-aware opcodes.
    /// </summary>
    public int GetPstrCodeUnitAt(int headerIdx, int i)
    {
        Cell header = _heap[headerIdx];
        if (header.Tag != Tag.Pstr)
            throw new InvalidOperationException($"Cell tag is {header.Tag}, expected Pstr.");
        return GetPstrCodeUnit(header, i);
    }

    /// <summary>
    /// Decomposes the PSTR header at heap[<paramref name="headerIdx"/>] into its first
    /// code unit and a tail header, updating the cell at <paramref name="headerIdx"/>
    /// in place. When the PSTR had one remaining code unit, the cell is replaced with
    /// the PSTR's own tail value (typically <c>Atom([])</c>); otherwise it's replaced
    /// with an advanced PSTR header sharing the original buffer.
    ///
    /// <para>The extracted head is returned as <c>Int(code_unit)</c> — only
    /// supports <c>codes</c> mode for <c>double_quotes</c>; the <c>chars</c> path is
    /// deferred until the flags subsystem lands.</para>
    /// </summary>
    /// <returns>true if a head was extracted; false if the cell was not a PSTR or had
    /// length zero (in which case no state is changed).</returns>
    public bool AdvancePstrHead(int headerIdx, out Cell head)
    {
        Cell hdr = _heap[headerIdx];
        if (hdr.Tag != Tag.Pstr || hdr.AsPstrLength == 0)
        {
            head = default;
            return false;
        }

        head = PstrHeadCell(hdr.AsPstrKind, GetPstrCodeUnit(hdr, 0));

        int newLength = hdr.AsPstrLength - 1;
        if (newLength == 0)
        {
            int tailIdx = ComputePstrTailIndex(hdr);
            _heap[headerIdx] = _heap[tailIdx];
        }
        else
        {
            int absoluteStart = hdr.AsPstrOffset + 1;
            int newBufferIdx = hdr.AsPstrBufferIndex + absoluteStart / Cell.PstrCodeUnitsPerBuffer;
            int newOffset = absoluteStart % Cell.PstrCodeUnitsPerBuffer;
            _heap[headerIdx] = Cell.Pstr(newLength, newBufferIdx, newOffset, hdr.AsPstrKind);
        }
        return true;
    }

    /// <summary>The element a packed list yields at a given position: a
    /// one-character atom or a code, per the header's <see cref="TextKind"/>.
    /// Every uncons path goes through here — the four of them producing their
    /// own head cell is how <c>X = "abc", X = [97,98,99]</c> came to fail
    /// through one cursor and succeed through another.</summary>
    private static Cell PstrHeadCell(TextKind kind, int codeUnit)
    {
        if (kind == TextKind.Codes) return Cell.Int(codeUnit);
        int cached = AtomTable.GetSingleCharAtomId(codeUnit);
        return Cell.Atom(cached >= 0
            ? cached
            : AtomTable.Intern(((char)codeUnit).ToString(), permanent: false).Id);
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

    // ----- Activation register accessors (read-only for now; set by the interpreter later) -----

    public int E => _e;
    public int B => _b;
    public int B0 => _b0;
    public int P => _p;
    public int Cp => _cp;

    /// <summary>True after a get_structure/get_list against an unbound argument, or
    /// after a put_structure/put_list — the interpreter is building a fresh compound
    /// and subsequent <c>unify_*</c> opcodes write cells. False when an existing
    /// compound on the heap is being matched against.</summary>
    public bool WriteMode => _writeMode;

    /// <summary>Heap index of the next cell <c>unify_*</c> will read (in read mode)
    /// or write (in write mode). Advances by one per <c>unify_*</c> instruction
    /// (or by <c>count</c> for <c>unify_void count</c>).</summary>
    public int UnifyPointer => _unifyPointer;

    // ----- Deref -----

    /// <summary>
    /// Walks REF cells starting at <paramref name="heapIdx"/> until reaching a non-REF cell
    /// or a self-pointing REF (unbound variable). Returns the final heap index. Reading
    /// <see cref="GetHeap"/> at the returned index yields the dereferenced cell.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
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

    // AggressiveInlining: with the capacity compare now inline
    // in EnsureBindingTrailCapacity this whole method flattens into the
    // Bind call sites as compare + store + increment.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void TrailBinding(int heapIdx)
    {
        EnsureBindingTrailCapacity(1);
        _bindingTrail[_bindingTrailTop++] = heapIdx;
    }

    // ============================================================================
    // Attributed variables
    // ============================================================================

    /// <summary>True iff the deref'd cell at <paramref name="heapAddr"/>
    /// is an attributed variable.</summary>
    public bool IsAttVar(int heapAddr) => _heap[Deref(heapAddr)].Tag == Tag.AttVar;

    /// <summary>Number of attribute records allocated — diagnostic surface.</summary>
    internal int AttrTableCount => _attrTable.Count;

    /// <summary>A snapshot of the attribute table's keys — the heap home of
    /// every variable that carries attributes, or carried them before it was
    /// bound.
    ///
    /// <para>Entries outlive the binding because backtracking restores the
    /// ATTVAR cell (<see cref="BindAttVarToValue"/> trails the original):
    /// dropping one at bind time would bring the variable back with its
    /// constraints gone. That is the reason, and the only one — in particular
    /// it is NOT for <c>call_residue_vars/2</c>, whose second half filters
    /// bound variables out itself.</para>
    ///
    /// <para>Callers diff two snapshots by raw heap address, which is sound
    /// only while addresses do not move — today because the heap collector
    /// stands down whenever the attribute table is non-empty. A collector that
    /// runs with attributed variables live has to relocate the saved snapshots
    /// as well.</para></summary>
    public int[] AttrTableKeysSnapshot()
    {
        var keys = new int[_attrTable.Count];
        _attrTable.Keys.CopyTo(keys, 0);
        return keys;
    }

    /// <summary>True when the heap cell at <paramref name="addr"/> is an
    /// (unbound) attributed variable.</summary>
    public bool IsAttVarAt(int addr) => GetHeap(addr).Tag == Tag.AttVar;

    /// <summary>Attaches (or replaces) the attribute for
    /// <paramref name="moduleId"/> on the variable at
    /// <paramref name="varAddr"/>, whose value lives at
    /// <paramref name="valueHeapIdx"/>. A plain unbound variable is
    /// promoted to an attributed variable; an existing attributed
    /// variable's record is updated. Every mutation is trailed so it
    /// reverts on backtracking. Throws when the target isn't a
    /// variable.</summary>
    public void PutAttr(int varAddr, int moduleId, int valueHeapIdx)
    {
        int addr = Deref(varAddr);
        Cell cell = _heap[addr];
        if (cell.Tag == Tag.Ref && cell.AsHeapIndex == addr)
        {
            // Plain unbound variable → promote to an attributed
            // variable. The cell change is trailed as a ValueChange so
            // backtracking restores the plain REF. A fresh record is
            // installed under the home index, overwriting any orphan
            // left by a backtracked-then-reused heap slot.
            TrailValueChange(addr, cell);
            _heap[addr] = Cell.AttVar(addr);
            _attrTable[addr] = new Dictionary<int, int>();
        }
        else if (cell.Tag != Tag.AttVar)
        {
            // ISO shape: the embedding layer renders Detail as the
            // expected-type atom, so error(type_error(var, _), _).
            throw new PrologRuntimeException("type_error", "var");
        }

        var record = _attrTable[addr];
        int oldValue = record.TryGetValue(moduleId, out int prev) ? prev : -1;
        TrailAttrChange(addr, moduleId, oldValue);
        record[moduleId] = valueHeapIdx;
    }

    /// <summary>Reads the attribute for <paramref name="moduleId"/> on
    /// the variable at <paramref name="varAddr"/>. Returns the heap
    /// index of the stored attribute value, or <c>-1</c> when the
    /// variable carries no such attribute (or isn't an attributed
    /// variable at all).</summary>
    public int GetAttr(int varAddr, int moduleId)
    {
        int addr = Deref(varAddr);
        if (_heap[addr].Tag != Tag.AttVar) return -1;
        var record = _attrTable[addr];
        return record.TryGetValue(moduleId, out int value) ? value : -1;
    }

    /// <summary>Removes the attribute for <paramref name="moduleId"/>
    /// from the variable at <paramref name="varAddr"/>. A no-op (still
    /// succeeds) when the variable isn't attributed or carries no such
    /// attribute. The removal is trailed.</summary>
    public void DelAttr(int varAddr, int moduleId)
    {
        int addr = Deref(varAddr);
        if (_heap[addr].Tag != Tag.AttVar) return;
        var record = _attrTable[addr];
        if (!record.TryGetValue(moduleId, out int oldValue)) return;
        TrailAttrChange(addr, moduleId, oldValue);
        record.Remove(moduleId);
        if (record.Count == 0)
        {
            // Last attribute gone → demote back to a plain unbound variable
            // (SWI semantics: attvar/1 is false again). Trailed like PutAttr's
            // promotion, so backtracking restores the AttVar cell — and with
            // it the record the trailed AttrChange entries repopulate.
            TrailValueChange(addr, _heap[addr]);
            _heap[addr] = Cell.UnboundVar(addr);
        }
    }

    /// <summary>The module ids that carry an attribute on the variable
    /// at <paramref name="varAddr"/> — empty when it isn't attributed.
    /// Used by the attvar-unification merge and by <c>copy_term/3</c>'s
    /// residual-goal projection.</summary>
    public IReadOnlyCollection<int> AttrModules(int varAddr)
    {
        int addr = Deref(varAddr);
        return _heap[addr].Tag == Tag.AttVar
            ? _attrTable[addr].Keys
            : Array.Empty<int>();
    }

    private void TrailAttrChange(int homeAddr, int moduleId, int oldValue)
    {
        int logIndex = _attrTrailLog.Count;
        _attrTrailLog.Add((homeAddr, moduleId, oldValue));
        EnsureExtraTrailCapacity(1);
        _extraTrail[_extraTrailTop++] = new ExtraTrailEntry
        {
            Type = TrailType.AttrModify,
            HeapIdx = logIndex,
            BindingTrailMarker = _bindingTrailTop,
        };
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
            case TrailType.BigIntAlloc:
                // entry.HeapIdx holds the table size *before* the allocation
                // that this trail entry records. Drop everything appended
                // since (in this case, exactly the one slot) so the table
                // is back to its pre-allocation shape.
                if (_bigIntTable.Count > entry.HeapIdx)
                    _bigIntTable.RemoveRange(entry.HeapIdx, _bigIntTable.Count - entry.HeapIdx);
                break;
            case TrailType.RationalAlloc:
                if (_rationalTable.Count > entry.HeapIdx)
                    _rationalTable.RemoveRange(entry.HeapIdx, _rationalTable.Count - entry.HeapIdx);
                break;
            case TrailType.MutableSet:
            {
                // entry.HeapIdx indexes _externalTrailLog. Unwind order is
                // LIFO, so this entry is the log's top; restore and truncate.
                var xlog = _externalTrailLog!;
                var (target, key, old, had) = xlog[entry.HeapIdx];
                target.RestoreExternal(key, old, had);
                xlog.RemoveRange(entry.HeapIdx, xlog.Count - entry.HeapIdx);
                break;
            }
            case TrailType.AttrModify:
                // entry.HeapIdx indexes _attrTrailLog, which records the
                // (attvar home, module, previous value) of one attribute
                // mutation. Restore the previous value, or remove the
                // module entirely when it was absent before (-1).
                {
                    var (home, mod, oldValue) = _attrTrailLog[entry.HeapIdx];
                    var record = _attrTable[home];
                    if (oldValue < 0) record.Remove(mod);
                    else record[mod] = oldValue;
                    // truncate the side log. entry.HeapIdx is the
                    // log index assigned at append time (TrailAttrChange),
                    // and extra-trail entries unwind strictly in reverse
                    // append order, so every log record at or above this
                    // index belongs to an entry already unwound (or dropped
                    // by a cut's CompactTrails, whose surviving entries keep
                    // their original — lower — indices). Without this the
                    // log grew unboundedly under clpfd labeling.
                    if (_attrTrailLog.Count > entry.HeapIdx)
                        _attrTrailLog.RemoveRange(
                            entry.HeapIdx, _attrTrailLog.Count - entry.HeapIdx);
                }
                break;
            case TrailType.CatchFrame:
                // Reverse a catch-frame stack operation. A push (recorded by
                // '$catch_begin') is undone by removing the top frame — by
                // the time this fires, every frame above it has already
                // been removed by its own entry. A deactivate (recorded by
                // '$catch_end') is undone by re-activating that frame, so
                // backtracking into a guarded goal restores its catcher.
                if (entry.OldValue.Data == CatchTrailPush)
                {
                    _catchFrames.RemoveAt(_catchFrames.Count - 1);
                }
                else
                {
                    CatchFrame f = _catchFrames[entry.HeapIdx];
                    f.Active = true;
                    _catchFrames[entry.HeapIdx] = f;
                }
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
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool Unify(int aIdx, int bIdx)
        => Unify(aIdx, bIdx, 0, null);

    /// <summary>C#-recursion depth past which plain unification escalates to
    /// the guarded (pair-set) mode. Typical terms never reach it and pay
    /// nothing; only deep nesting — or a CYCLIC pair, whose recursion would
    /// otherwise overflow the C# stack — escalates. Well below the
    /// stack-overflow point (same scheme as the standard-order comparator).</summary>
    private const int UnifyRecursionLimit = 512;

    /// <summary>List-spine iterations past which <see cref="UnifyLis"/>
    /// engages the pair guard: a CYCLIC spine loops forever WITHOUT growing
    /// the C# stack, so the depth limit alone cannot catch it. High enough
    /// that real lists (under a million elements) never pay the guard.</summary>
    private const int UnifySpineGuardLimit = 1 << 20;

    // Guarded mode: `activePairs` holds every compound pair currently being
    // (or already) unified in this walk. Re-encountering a pair means the
    // equation is already in the system — assume it holds and move on
    // (rational-tree unification, the coinductive reading SWI implements:
    // X = f(X), Y = f(Y), X = Y is true). Pairs are kept, not removed, so
    // shared (DAG) subterms also unify once.
    private bool Unify(int aIdx, int bIdx, int depth, HashSet<long>? activePairs)
    {
        Profiler.Unify();
        if (activePairs is null && depth >= UnifyRecursionLimit)
            activePairs = new HashSet<long>();
        int aAddr = Deref(aIdx);
        int bAddr = Deref(bIdx);
        if (aAddr == bAddr) return true;

        Cell aCell = _heap[aAddr];
        Cell bCell = _heap[bAddr];

        // Attributed variables participate in cross-tag
        // unification, so dispatch them before the plain-REF handling.
        if (aCell.Tag == Tag.AttVar || bCell.Tag == Tag.AttVar)
            return UnifyAttVar(aAddr, aCell, bAddr, bCell);

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
            Tag.Str => UnifyStr(aCell.AsHeapIndex, bCell.AsHeapIndex, depth, activePairs),
            Tag.Lis => UnifyLis(aCell.AsHeapIndex, bCell.AsHeapIndex, depth, activePairs),
            Tag.BigInt => _bigIntTable[aCell.AsBigIntId].Equals(_bigIntTable[bCell.AsBigIntId]),
            Tag.Rational => _rationalTable[aCell.AsRationalId].Equals(_rationalTable[bCell.AsRationalId]),
            Tag.String => string.Equals(_stringTable[aCell.AsStringId], _stringTable[bCell.AsStringId]),
            Tag.Foreign => ReferenceEquals(_foreignTable[aCell.AsForeignId], _foreignTable[bCell.AsForeignId]),
            Tag.Float => UnifyFloat(aCell, bCell),
            _ => throw new InvalidOperationException($"Unify reached cell with unexpected tag {aCell.Tag}."),
        };
    }

    /// <summary>
    /// Cell-based unification (ADR-017). Unifies two operand cells taken
    /// directly from registers / Y-slots / bytecode literals, WITHOUT first
    /// copying an inline compound (Str/Lis) to a heap address — which is what
    /// the materialise-then-<see cref="Unify(int,int)"/> entry points used to
    /// do, re-copying a register-held structure on every <c>get_value</c> and
    /// quadratically under backtracking (the zebra regression that blocked
    /// ADR-017 phase 2). The structure's functor/args already live on the
    /// heap; only its tag rides in the operand cell, so binding a variable to
    /// it copies just the one tag cell into the variable's existing home
    /// (zero allocation), and unifying two compounds recurses on their heap
    /// args via the address-based path. Attributed variables and partial
    /// strings — which genuinely need both operands at heap addresses — fall
    /// back to materialise-then-Unify (rare; an inline operand there is
    /// copied once).
    /// </summary>
    private bool UnifyCells(Cell ca, Cell cb)
    {
        var (a, aAddr) = ResolveOperand(ca);
        var (b, bAddr) = ResolveOperand(cb);
        if (aAddr >= 0 && aAddr == bAddr) return true;

        // AttVar / PSTR need heap addresses for both sides — defer to the
        // address-based path, materialising an inline operand if necessary.
        if (a.Tag is Tag.AttVar or Tag.Pstr || b.Tag is Tag.AttVar or Tag.Pstr)
        {
            int ha = aAddr >= 0 ? aAddr : MaterializeCell(a);
            int hb = bAddr >= 0 ? bAddr : MaterializeCell(b);
            return Unify(ha, hb);
        }

        if (a.Tag == Tag.Ref)            // a is an unbound variable at aAddr
        {
            if (b.Tag == Tag.Ref) BindVarToVar(aAddr, bAddr);
            else BindVarToCellValue(aAddr, b, bAddr);
            return true;
        }
        if (b.Tag == Tag.Ref)            // b is an unbound variable at bAddr
        {
            BindVarToCellValue(bAddr, a, aAddr);
            return true;
        }

        // Both bound values.
        if (a.Tag != b.Tag) return false;
        return a.Tag switch
        {
            Tag.Atom => a.AsAtomId == b.AsAtomId,
            Tag.Int => a.AsInt == b.AsInt,
            Tag.Str => UnifyStr(a.AsHeapIndex, b.AsHeapIndex),
            Tag.Lis => UnifyLis(a.AsHeapIndex, b.AsHeapIndex),
            Tag.BigInt => _bigIntTable[a.AsBigIntId].Equals(_bigIntTable[b.AsBigIntId]),
            Tag.Rational => _rationalTable[a.AsRationalId].Equals(_rationalTable[b.AsRationalId]),
            Tag.String => string.Equals(_stringTable[a.AsStringId], _stringTable[b.AsStringId]),
            Tag.Foreign => ReferenceEquals(_foreignTable[a.AsForeignId], _foreignTable[b.AsForeignId]),
            Tag.Float => UnifyFloat(a, b),
            _ => throw new InvalidOperationException($"UnifyCells reached unexpected tag {a.Tag}."),
        };
    }

    /// <summary>Resolves an operand cell to its effective (dereferenced) cell
    /// and heap home. A REF / ATTVAR is followed to its heap home (returning
    /// that address); any other cell is an inline value returned as-is with
    /// address -1 (its Str/Lis referents are reachable via its payload, so it
    /// needs no home of its own).</summary>
    private (Cell cell, int addr) ResolveOperand(Cell c)
    {
        if (c.Tag is Tag.Ref or Tag.AttVar)
        {
            int addr = Deref(c.AsHeapIndex);
            return (_heap[addr], addr);
        }
        return (c, -1);
    }

    /// <summary>Binds the unbound variable at <paramref name="varAddr"/> to a
    /// value cell. For a compound (Str/Lis) the variable receives a REF to the
    /// value's heap home when it has one, or the inline compound cell copied
    /// directly into its own home when it does not (zero allocation — the
    /// ADR-017 win); atomic values are copied in place.</summary>
    private void BindVarToCellValue(int varAddr, Cell value, int valueAddr)
    {
        if (value.Tag is Tag.Str or Tag.Lis)
            Bind(varAddr, valueAddr >= 0 ? Cell.Ref(valueAddr) : value);
        else
            Bind(varAddr, value);
    }

    /// <summary>Copies an inline value cell to a fresh heap slot and returns
    /// its address. Only used by <see cref="UnifyCells"/>'s AttVar/PSTR
    /// fallback for the rare case of an inline operand of that kind.</summary>
    private int MaterializeCell(Cell c)
    {
        int slot = AllocateHeap(1);
        _heap[slot] = c;
        return slot;
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
    private bool UnifyStr(int fA, int fB, int depth = 0, HashSet<long>? activePairs = null)
    {
        int functorIdA = _heap[fA].AsFunctorId;
        int functorIdB = _heap[fB].AsFunctorId;
        if (functorIdA != functorIdB) return false;
        if (activePairs is not null
            && !activePairs.Add(((long)fA << 32) | (uint)fB))
            return true;   // pair already in the system — rational-tree unify
        var (_, arity) = FunctorTable.Lookup(functorIdA);
        for (int i = 1; i <= arity; i++)
        {
            if (!Unify(fA + i, fB + i, depth + 1, activePairs)) return false;
        }
        return true;
    }

    /// <summary>
    /// Unifies two cons cells. <paramref name="hA"/> and <paramref name="hB"/> are heap
    /// indices of head cells (the payloads of their containing LIS cells); the matching
    /// tail cells live immediately after.
    /// </summary>
    private bool UnifyLis(int hA, int hB, int depth = 0, HashSet<long>? activePairs = null)
    {
        // walk the list spine iteratively. The previous shape
        // (Unify head, then Unify tails re-entering UnifyLis) recursed one
        // C# frame per element, so a long list risked the stack overflow
        // acknowledged in UnifyStr's doc note. Heads still unify through
        // the normal recursive call (nesting depth, not spine length); the
        // tails are deref'd here and the loop continues while both remain
        // cons cells, delegating anything else to the general Unify.
        //
        // A CYCLIC spine loops here WITHOUT growing the C# stack, so past
        // UnifySpineGuardLimit iterations the pair guard engages: a revisited
        // cons pair is an equation already in the system — rational-tree true.
        int spineIters = 0;
        while (true)
        {
            if (activePairs is not null
                && !activePairs.Add(((long)hA << 32) | (uint)hB))
                return true;   // cyclic spine — rational-tree unify
            if (!Unify(hA, hB, depth + 1, activePairs)) return false;
            int aAddr = Deref(hA + 1);
            int bAddr = Deref(hB + 1);
            if (aAddr == bAddr) { Profiler.Unify(); return true; }   // the tail Unify the recursion used to count
            Cell aCell = _heap[aAddr];
            Cell bCell = _heap[bAddr];
            if (aCell.Tag == Tag.Lis && bCell.Tag == Tag.Lis)
            {
                Profiler.Unify();   // the tail Unify the recursion used to count
                hA = aCell.AsHeapIndex;
                hB = bCell.AsHeapIndex;
                if (activePairs is null && ++spineIters >= UnifySpineGuardLimit)
                    activePairs = new HashSet<long>();
                continue;
            }
            return Unify(aAddr, bAddr, depth + 1, activePairs);
        }
    }

    /// <summary>Cell-based occurs-check unification (ADR-017) — the
    /// occurs-check counterpart of <see cref="UnifyCells"/>. Var-to-var and
    /// both-bound cases unify the operand cells directly without
    /// materialising; only binding a variable to an inline compound needs the
    /// value at a heap address (for <see cref="OccursIn"/>), so it
    /// materialises that one operand — rare, since occurs-check unification is
    /// itself rare (<c>unify_with_occurs_check/2</c>).</summary>
    private bool UnifyCellsWithOccursCheck(Cell ca, Cell cb)
    {
        var (a, aAddr) = ResolveOperand(ca);
        var (b, bAddr) = ResolveOperand(cb);
        if (aAddr >= 0 && aAddr == bAddr) return true;

        if (a.Tag is Tag.AttVar or Tag.Pstr || b.Tag is Tag.AttVar or Tag.Pstr)
        {
            int ha = aAddr >= 0 ? aAddr : MaterializeCell(a);
            int hb = bAddr >= 0 ? bAddr : MaterializeCell(b);
            return UnifyWithOccursCheck(ha, hb);
        }

        if (a.Tag == Tag.Ref)            // a is an unbound variable at aAddr
        {
            if (b.Tag == Tag.Ref) { BindVarToVar(aAddr, bAddr); return true; }
            int hb = bAddr >= 0 ? bAddr : MaterializeCell(b);
            if (OccursIn(aAddr, hb)) return false;
            BindVarToValue(aAddr, hb, _heap[hb]);
            return true;
        }
        if (b.Tag == Tag.Ref)            // b is an unbound variable at bAddr
        {
            int ha = aAddr >= 0 ? aAddr : MaterializeCell(a);
            if (OccursIn(bAddr, ha)) return false;
            BindVarToValue(bAddr, ha, _heap[ha]);
            return true;
        }

        if (a.Tag != b.Tag) return false;
        return a.Tag switch
        {
            Tag.Atom => a.AsAtomId == b.AsAtomId,
            Tag.Int => a.AsInt == b.AsInt,
            Tag.Str => UnifyStrWithOccursCheck(a.AsHeapIndex, b.AsHeapIndex),
            Tag.Lis => UnifyLisWithOccursCheck(a.AsHeapIndex, b.AsHeapIndex),
            Tag.BigInt => _bigIntTable[a.AsBigIntId].Equals(_bigIntTable[b.AsBigIntId]),
            Tag.Rational => _rationalTable[a.AsRationalId].Equals(_rationalTable[b.AsRationalId]),
            Tag.String => string.Equals(_stringTable[a.AsStringId], _stringTable[b.AsStringId]),
            Tag.Foreign => ReferenceEquals(_foreignTable[a.AsForeignId], _foreignTable[b.AsForeignId]),
            Tag.Float => UnifyFloat(a, b),
            _ => throw new InvalidOperationException($"UnifyCellsWithOccursCheck reached unexpected tag {a.Tag}."),
        };
    }

    /// <summary>
    /// The occurs-check variant of <see cref="Unify"/> — ISO §8.2.2's
    /// <c>unify_with_occurs_check/2</c>. Identical to <see cref="Unify"/>
    /// except that before binding a variable to a compound term, it
    /// verifies that the variable's heap cell does not occur anywhere
    /// inside that term; if it does, the unification fails. This rules
    /// out the cyclic terms plain <c>=/2</c> would build (e.g.
    /// <c>X = f(X)</c>), at the cost of one structural walk per bind.
    /// </summary>
    public bool UnifyWithOccursCheck(int aIdx, int bIdx)
        => UnifyWithOccursCheck(aIdx, bIdx, activePairs: null);

    private bool UnifyWithOccursCheck(int aIdx, int bIdx, HashSet<long>? activePairs)
    {
        int aAddr = Deref(aIdx);
        int bAddr = Deref(bIdx);
        if (aAddr == bAddr) return true;

        Cell aCell = _heap[aAddr];
        Cell bCell = _heap[bAddr];

        // Attributed variables follow the same hook path as the
        // standard unify — the occurs check is enforced before the
        // hook fires, identical to SWI's semantics.
        if (aCell.Tag == Tag.AttVar || bCell.Tag == Tag.AttVar)
        {
            // For attvars we fall back to the regular unify path; the
            // occurs-check still applies to any plain Ref binding the
            // hook hasn't intercepted. (Strict ISO occurs-check over
            // attvar hooks is not in the standard.)
            return UnifyAttVar(aAddr, aCell, bAddr, bCell);
        }

        if (aCell.Tag == Tag.Ref)
        {
            if (bCell.Tag == Tag.Ref)
            {
                // Var-to-var binding is safe — no cycle possible.
                BindVarToVar(aAddr, bAddr);
                return true;
            }
            if (OccursIn(aAddr, bAddr)) return false;
            BindVarToValue(aAddr, bAddr, bCell);
            return true;
        }
        if (bCell.Tag == Tag.Ref)
        {
            if (OccursIn(bAddr, aAddr)) return false;
            BindVarToValue(bAddr, aAddr, aCell);
            return true;
        }

        if (aCell.Tag == Tag.Pstr) return UnifyPstr(aAddr, bAddr);
        if (bCell.Tag == Tag.Pstr) return UnifyPstr(bAddr, aAddr);

        if (aCell.Tag != bCell.Tag) return false;
        return aCell.Tag switch
        {
            Tag.Atom => aCell.AsAtomId == bCell.AsAtomId,
            Tag.Int => aCell.AsInt == bCell.AsInt,
            Tag.Str => UnifyStrWithOccursCheck(aCell.AsHeapIndex, bCell.AsHeapIndex, activePairs),
            Tag.Lis => UnifyLisWithOccursCheck(aCell.AsHeapIndex, bCell.AsHeapIndex, activePairs),
            Tag.BigInt => _bigIntTable[aCell.AsBigIntId].Equals(_bigIntTable[bCell.AsBigIntId]),
            Tag.Rational => _rationalTable[aCell.AsRationalId].Equals(_rationalTable[bCell.AsRationalId]),
            Tag.String => string.Equals(_stringTable[aCell.AsStringId], _stringTable[bCell.AsStringId]),
            Tag.Foreign => ReferenceEquals(_foreignTable[aCell.AsForeignId], _foreignTable[bCell.AsForeignId]),
            Tag.Float => UnifyFloat(aCell, bCell),
            _ => throw new InvalidOperationException($"UnifyWithOccursCheck reached cell with unexpected tag {aCell.Tag}."),
        };
    }

    // The compound walk threads an active-pair set so unifying two CYCLIC
    // terms (X = f(X), Y = f(Y), unify_with_occurs_check(X, Y)) terminates:
    // re-entering a pair already on the walk's path means the unification
    // could only succeed by building an infinite tree, which sound
    // unification must reject — so it FAILS (SWI behaves the same).
    private bool UnifyStrWithOccursCheck(int fA, int fB, HashSet<long>? activePairs = null)
    {
        int functorIdA = _heap[fA].AsFunctorId;
        int functorIdB = _heap[fB].AsFunctorId;
        if (functorIdA != functorIdB) return false;
        long pairKey = ((long)fA << 32) | (uint)fB;
        activePairs ??= new HashSet<long>();
        if (!activePairs.Add(pairKey)) return false;   // cyclic pair → fail
        var (_, arity) = FunctorTable.Lookup(functorIdA);
        bool ok = true;
        for (int i = 1; i <= arity && ok; i++)
            ok = UnifyWithOccursCheck(fA + i, fB + i, activePairs);
        activePairs.Remove(pairKey);
        return ok;
    }

    private bool UnifyLisWithOccursCheck(int hA, int hB, HashSet<long>? activePairs = null)
    {
        long pairKey = ((long)hA << 32) | (uint)hB;
        activePairs ??= new HashSet<long>();
        if (!activePairs.Add(pairKey)) return false;   // cyclic pair → fail
        bool ok = UnifyWithOccursCheck(hA, hB, activePairs)
               && UnifyWithOccursCheck(hA + 1, hB + 1, activePairs);
        activePairs.Remove(pairKey);
        return ok;
    }

    /// <summary>True iff the variable cell at <paramref name="targetAddr"/>
    /// is structurally reachable from the (dereferenced) value at
    /// <paramref name="sourceAddr"/> — OR that value is CYCLIC. Both mean
    /// the bind must be rejected: sound unification only ever produces
    /// finite trees, so binding a variable to an already-cyclic term fails
    /// (SWI behaves the same; a naive walk would loop forever). Walks the
    /// source iteratively over an explicit stack (no C# recursion), with an
    /// on-path set for cycle detection (a negative stack entry is the exit
    /// marker that leaves the path) and a done set so shared (DAG) subterms
    /// are checked once, not re-flagged as cycles.</summary>
    private bool OccursIn(int targetAddr, int sourceAddr)
    {
        var stack = new Stack<int>();
        var onPath = new HashSet<int>();
        HashSet<int>? done = null;
        stack.Push(sourceAddr);
        while (stack.Count > 0)
        {
            int raw = stack.Pop();
            if (raw < 0) { onPath.Remove(~raw); continue; }   // exit marker
            int addr = Deref(raw);
            if (addr == targetAddr) return true;
            Cell c = _heap[addr];
            switch (c.Tag)
            {
                case Tag.Str:
                {
                    int fIdx = c.AsHeapIndex;
                    if (onPath.Contains(fIdx)) return true;   // cyclic source
                    if (!(done ??= new HashSet<int>()).Add(fIdx)) break;
                    onPath.Add(fIdx);
                    stack.Push(~fIdx);
                    int functorId = _heap[fIdx].AsFunctorId;
                    var (_, arity) = FunctorTable.Lookup(functorId);
                    for (int i = 1; i <= arity; i++) stack.Push(fIdx + i);
                    break;
                }
                case Tag.Lis:
                {
                    int hIdx = c.AsHeapIndex;
                    if (onPath.Contains(hIdx)) return true;   // cyclic source
                    if (!(done ??= new HashSet<int>()).Add(hIdx)) break;
                    onPath.Add(hIdx);
                    stack.Push(~hIdx);
                    stack.Push(hIdx);
                    stack.Push(hIdx + 1);
                    break;
                }
                case Tag.Pstr:
                {
                    // PSTR characters are immediate ints — only the
                    // logical tail can carry a variable.
                    if (onPath.Contains(addr)) return true;   // cyclic source
                    if (!(done ??= new HashSet<int>()).Add(addr)) break;
                    onPath.Add(addr);
                    stack.Push(~addr);
                    int tailIdx = ComputePstrTailIndex(c);
                    stack.Push(tailIdx);
                    break;
                }
                case Tag.AttVar:
                    // An attvar is a kind of variable; the comparison
                    // by address above covers the identity case.
                    // Attribute pairs live in a side table, not the
                    // heap, so we don't traverse them here — matching
                    // the standard occurs-check semantics.
                    break;
                // Atoms, Ints, Floats, BigInts, Strings, Foreigns: leaves.
            }
        }
        return false;
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
        // Value equality, so -0.0 unifies with 0.0 (IEEE: -0.0 == 0.0),
        // matching ==/2's value comparison (`number_chars(0.0, "-0.0")` must
        // succeed). The NaN clause keeps a NaN unifying with a NaN — value
        // `==` is false for NaN, but there is conceptually one NaN in Prolog.
        return a == b || (double.IsNaN(a) && double.IsNaN(b));
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

        // [a,b,c] and [97,98,99] are different lists. An empty segment carries
        // no elements, so its presentation says nothing and must not decide.
        if (aLen > 0 && bLen > 0 && aHdr.AsPstrKind != bHdr.AsPstrKind)
            return false;

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
        _heap[sliceSlot] = Cell.Pstr(sliceLength, sliceBufferIdx, sliceOffset, bHdr.AsPstrKind);
        return Unify(aTailIdx, sliceSlot);
    }

    /// <summary>
    /// Unifies a non-empty PSTR with a cons cell. Decomposes the first code unit as
    /// <c>Int(cu)</c>, then recurses on the LIS tail with either the PSTR's stored tail
    /// (length 1) or a virtual one-shorter PSTR slice.
    ///
    /// <para>Heads are emitted as 16-bit UTF-16 code units; supplementary codepoints
    /// (above U+FFFF) appear as two separate surrogate values rather than one combined
    /// codepoint. This is enough for BMP-only grammar workloads;
    /// surrogate-pair fusion is a future refinement.</para>
    /// </summary>
    private bool UnifyPstrLis(int pstrAddr, int lisAddr)
    {
        Cell pstrHdr = _heap[pstrAddr];
        int length = pstrHdr.AsPstrLength;

        int lisHeadIdx = _heap[lisAddr].AsHeapIndex;
        int lisTailIdx = lisHeadIdx + 1;

        int headSlot = AllocateHeap(1);
        _heap[headSlot] = PstrHeadCell(pstrHdr.AsPstrKind, GetPstrCodeUnit(pstrHdr, 0));
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
        _heap[sliceSlot] = Cell.Pstr(length - 1, newBufferIdx, newOffset, pstrHdr.AsPstrKind);
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

    /// <summary>Unifies when at least one side is an attributed
    /// variable. Three cases:
    /// <list type="bullet">
    /// <item>attvar + plain unbound REF — the plain variable binds to
    /// the attvar, which survives carrying its attributes.</item>
    /// <item>attvar + attvar — the younger binds to the older and the
    /// younger's attributes merge onto the older's record; a module
    /// present on both must have unifiable values.</item>
    /// <item>attvar + bound value — the attvar binds to the value, and
    /// a wakeup is queued so the next goal boundary runs the module's
    /// <c>verify_attributes/4</c> hook.</item>
    /// </list></summary>
    private bool UnifyAttVar(int aAddr, Cell aCell, int bAddr, Cell bCell)
    {
        bool aAtt = aCell.Tag == Tag.AttVar;
        bool bAtt = bCell.Tag == Tag.AttVar;

        if (aAtt && bAtt)
        {
            int olderAddr = aAddr < bAddr ? aAddr : bAddr;
            int youngerAddr = aAddr < bAddr ? bAddr : aAddr;
            if (!MergeAttributes(youngerAddr, olderAddr)) return false;
            // The younger variable is the one being bound (to the
            // older); queue its modules' hooks with the older variable
            // as the "other" term.
            QueueAttrWakeups(youngerAddr, olderAddr);
            BindAttVarToValue(youngerAddr, olderAddr, Cell.Ref(olderAddr));
            return true;
        }

        int attAddr = aAtt ? aAddr : bAddr;
        int otherAddr = aAtt ? bAddr : aAddr;
        Cell otherCell = aAtt ? bCell : aCell;

        if (otherCell.Tag == Tag.Ref)
        {
            // Plain unbound variable binds to the attvar — the attvar
            // (with its attributes) survives. Nothing is bound to a
            // value, so no hook fires; a normal trailed Bind is right.
            Bind(otherAddr, Cell.Ref(attAddr));
            return true;
        }

        // attvar + bound value: queue the attvar's modules' hooks (with
        // the value as the "other" term), then bind. The interpreter
        // runs the queued hooks at the next goal boundary.
        QueueAttrWakeups(attAddr, otherAddr);
        BindAttVarToValue(attAddr, otherAddr, otherCell);
        return true;
    }

    /// <summary>Queues one unify-hook wakeup per attribute module
    /// carried by the attributed variable at
    /// <paramref name="attvarHome"/>, recording the term it was bound
    /// to (<paramref name="otherIdx"/>). A no-op when the variable
    /// carries no attributes.</summary>
    /// <para>Called from the head-matching ops too (<c>get_struct</c>,
    /// <c>get_list</c> and their unify-cursor twins): binding an attributed
    /// variable by MATCHING a clause head against it is a binding like any
    /// other, and used not to wake anything — so a <c>freeze/2</c> on a
    /// variable that a callee's head decomposed never fired.</para>
    private void QueueAttrWakeups(int attvarHome, int otherIdx)
    {
        if (!_attrTable.TryGetValue(attvarHome, out var record)) return;
        foreach (var (moduleId, attrValueIdx) in record)
            _pendingWakeups.Add((moduleId, attrValueIdx, otherIdx));
    }

    /// <summary>True when attribute hooks are queued and waiting to run.
    /// The interpreter checks this at every goal boundary.</summary>
    public bool HasPendingWakeups => _pendingWakeups.Count > 0;

    /// <summary>Set by the bytecode interpreter so Tier-1 IL code can run
    /// pending <c>verify_attributes</c> wakeups through the interpreter's
    /// goal-running machinery (which the IL delegate, holding only an
    /// <see cref="Activation"/>, cannot reach directly). Returns false when a
    /// hook failed.</summary>
    internal Func<bool>? Tier1WakeupFlusher { get; set; }

    /// <summary>Tier-1 IL cut support. A cut is a goal boundary:
    /// any wakeup queued by the IL clause body (e.g. binding a clpfd attvar
    /// in the head, then a neck cut) must run BEFORE the cut commits, or a
    /// failing constraint has no surviving choice point to backtrack into —
    /// the same unsoundness the bytecode interpreter had to fix.
    /// The IL emit calls this immediately before <see cref="NeckCut"/> /
    /// <see cref="CutToLevel"/>; a false result means a wakeup failed and the
    /// caller must branch to its fail label instead of cutting. The
    /// <c>_pendingWakeups.Count</c> fast path keeps the overwhelmingly common
    /// no-wakeup case (every non-attvar program) to a single field read.
    ///
    /// <para>FUTURE (deferred, kept on purpose): this runtime guard costs
    /// ~1-2.5 ns per IL cut even when no attribute hook exists. It could be
    /// elided entirely by gating the IL EMISSION on
    /// <see cref="HasAnyAttributeHook"/> at promotion time (zero cost for
    /// non-attvar IL programs). That was NOT done because it needs a
    /// soundness-critical invariant — the IL promotion cache must be
    /// invalidated whenever ANY consult first defines
    /// <c>verify_attributes/4</c> (not just UseClpfd/UseClpr), or a predicate
    /// promoted before a custom hook loaded would silently skip the flush. The
    /// ~ns-per-cut cost is below the wall-clock noise floor and only applies to
    /// opt-in Tier-1 IL, so the simple always-on runtime guard wins for now.</para>
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool FlushWakeupsForIlCut()
    {
        if (_pendingWakeups.Count == 0) return true;
        return Tier1WakeupFlusher is null || Tier1WakeupFlusher();
    }

    /// <summary>Discards every queued wakeup. Called by the interpreter
    /// on backtracking — wakeups belong to the abandoned computation.</summary>
    public void ClearPendingWakeups() => _pendingWakeups.Clear();

    /// <summary>The number of queued wakeups — snapshot before a trial
    /// unification so <see cref="TruncatePendingWakeups"/> can discard the
    /// wakeups the trial queued after its bindings are unwound.</summary>
    public int PendingWakeupCount => _pendingWakeups.Count;

    /// <summary>Discards the wakeups queued after <paramref name="count"/>
    /// was captured. A trial unification (<c>\=/2</c>, <c>dif/2</c>) unwinds
    /// its bindings, so running the hooks it queued would fire
    /// <c>verify_attributes/4</c> for bindings that no longer exist.</summary>
    public void TruncatePendingWakeups(int count)
    {
        if (_pendingWakeups.Count > count)
            _pendingWakeups.RemoveRange(count, _pendingWakeups.Count - count);
    }

    /// <summary>The saved machine state a trial unification must restore.
    /// Returned by <see cref="BeginTrialUnify"/> and consumed by
    /// <see cref="EndTrialUnify"/> — the caller may materialise the live
    /// bindings in between (which <see cref="EndTrialUnify"/> then rolls
    /// back).</summary>
    public readonly struct TrialUnifyScope
    {
        internal readonly int HeapTop, BindingTrail, ExtraTrail, Hb, Wakeups;
        internal TrialUnifyScope(int heapTop, int bindingTrail, int extraTrail,
            int hb, int wakeups)
        {
            HeapTop = heapTop; BindingTrail = bindingTrail; ExtraTrail = extraTrail;
            Hb = hb; Wakeups = wakeups;
        }
    }

    /// <summary>Trial-unifies registers <paramref name="regA"/> and
    /// <paramref name="regB"/>, LEAVING the bindings in place so the caller
    /// can read the unifier (e.g. materialise each bound variable's value).
    /// The caller MUST call <see cref="EndTrialUnify"/> with the returned
    /// <paramref name="scope"/> to roll the bindings back. Returns false when
    /// the terms cannot unify — in which case the rollback is done here and
    /// <paramref name="scope"/> must not be used. On success,
    /// <paramref name="boundVars"/> holds the distinct heap addresses of the
    /// pre-existing variables (plain or attributed) the trial bound.</summary>
    public bool BeginTrialUnify(int regA, int regB,
        out List<int> boundVars, out TrialUnifyScope scope)
    {
        scope = new TrialUnifyScope(_heapTop, _bindingTrailTop, _extraTrailTop,
            _hb, _pendingWakeups.Count);

        // Hb at the heap top makes every trial binding trail — even to "old"
        // variables — which is both what the unwind needs and what lets the
        // trail double as the record of WHICH variables the trial bound.
        SetHb(_heapTop);
        bool unified = UnifyRegisters(regA, regB);

        boundVars = new List<int>();
        if (!unified)
        {
            EndTrialUnify(scope);
            return false;
        }

        var seen = new HashSet<int>();
        for (int i = scope.BindingTrail; i < _bindingTrailTop; i++)
        {
            int addr = _bindingTrail[i];
            if (addr < scope.HeapTop && seen.Add(addr)) boundVars.Add(addr);
        }
        // Attributed variables bind via a ValueChange entry carrying the
        // original ATTVAR cell, not a plain binding-trail address.
        for (int i = scope.ExtraTrail; i < _extraTrailTop; i++)
        {
            ref var e = ref _extraTrail[i];
            if (e.Type == TrailType.ValueChange && e.OldValue.Tag == Tag.AttVar
                && e.HeapIdx < scope.HeapTop && seen.Add(e.HeapIdx))
                boundVars.Add(e.HeapIdx);
        }
        return true;
    }

    /// <summary>Rolls back a trial unification begun by
    /// <see cref="BeginTrialUnify"/>: bindings, heap top, Hb and any
    /// attribute-hook wakeups the trial queued.</summary>
    public void EndTrialUnify(TrialUnifyScope scope)
    {
        UnwindTrails(scope.BindingTrail, scope.ExtraTrail);
        SetHeapTop(scope.HeapTop);
        SetHb(scope.Hb);
        TruncatePendingWakeups(scope.Wakeups);
    }

    /// <summary>Trial-unifies the terms in registers <paramref name="regA"/>
    /// and <paramref name="regB"/>, then rolls everything back — bindings,
    /// heap top, Hb and any attribute-hook wakeups the trial queued. Returns
    /// false when the terms cannot unify. When they can,
    /// <paramref name="unifierVars"/> receives the distinct heap addresses of
    /// every real variable participating in the unifier: the variables the
    /// trial bound PLUS the unbound variables inside the values they were
    /// bound to. Both sides matter — a <c>dif/2</c> suspension attributes
    /// every one of them, because a plain variable aliasing to an attributed
    /// one fires no hook (the attvar survives), so leaving the value-side
    /// variable plain would let the pair become identical silently. An empty
    /// list means the terms are already identical.</summary>
    public bool TrialUnifyCollectingBoundVars(int regA, int regB,
        out List<int> unifierVars)
    {
        if (!BeginTrialUnify(regA, regB, out unifierVars, out var scope))
            return false;

        // Value-side variables — walked BEFORE the unwind, while the
        // bindings are still in place. The walk appends to unifierVars.
        // Separate visited set for structure cells: a var home can BE a
        // list-pair head slot, and sharing the set would skip that pair.
        var seen = new HashSet<int>(unifierVars);
        var visited = new HashSet<int>();
        int boundCount = unifierVars.Count;
        for (int i = 0; i < boundCount; i++)
            CollectUnboundVars(GetHeap(unifierVars[i]), scope.HeapTop,
                unifierVars, seen, visited);

        EndTrialUnify(scope);
        return true;
    }

    /// <summary>Appends to <paramref name="into"/> the distinct unbound
    /// variables (plain or attributed) reachable from <paramref name="cell"/>
    /// that live below <paramref name="heapLimit"/>. The shared visited set
    /// also stops cyclic terms.</summary>
    private void CollectUnboundVars(Cell cell, int heapLimit,
        List<int> into, HashSet<int> seen, HashSet<int> visited)
    {
        while (cell.Tag == Tag.Ref)
        {
            int addr = Deref(cell.AsHeapIndex);
            Cell at = GetHeap(addr);
            if (at.Tag == Tag.Ref && at.AsHeapIndex == addr)
            {
                if (addr < heapLimit && seen.Add(addr)) into.Add(addr);
                return;
            }
            cell = at;
        }
        switch (cell.Tag)
        {
            case Tag.AttVar:
                int va = cell.AsHeapIndex;
                if (va < heapLimit && seen.Add(va)) into.Add(va);
                break;
            case Tag.Str:
                int fIdx = cell.AsHeapIndex;
                if (!visited.Add(fIdx)) break;
                var (_, arity) = FunctorTable.Lookup(GetHeap(fIdx).AsFunctorId);
                for (int i = 0; i < arity; i++)
                    CollectUnboundVars(GetHeap(fIdx + 1 + i), heapLimit, into, seen, visited);
                break;
            case Tag.Lis:
                int h = cell.AsHeapIndex;
                if (!visited.Add(h)) break;
                CollectUnboundVars(GetHeap(h), heapLimit, into, seen, visited);
                CollectUnboundVars(GetHeap(h + 1), heapLimit, into, seen, visited);
                break;
        }
    }

    /// <summary>Returns the queued wakeups and empties the queue. The
    /// interpreter drains them at a goal boundary, building a
    /// <c>verify_attributes/4</c> goal per entry and meta-calling it in
    /// this (live) engine so the hooks see the real attributed
    /// variables.</summary>
    public IReadOnlyList<(int Module, int AttrValueIdx, int OtherIdx)> TakePendingWakeups()
    {
        var taken = _pendingWakeups.ToArray();
        _pendingWakeups.Clear();
        return taken;
    }

    /// <summary>Binds the attributed variable at <paramref name="attAddr"/>
    /// to <paramref name="valueCell"/>. Unlike a plain bind this trails
    /// a <see cref="TrailType.ValueChange"/> carrying the original
    /// ATTVAR cell, so backtracking restores the attributed variable
    /// (not a bare unbound REF).</summary>
    private void BindAttVarToValue(int attAddr, int valueAddr, Cell valueCell)
    {
        Cell newCell = valueCell.Tag is Tag.Str or Tag.Lis or Tag.Pstr
            ? Cell.Ref(valueAddr)
            : valueCell;
        TrailValueChange(attAddr, _heap[attAddr]);
        _heap[attAddr] = newCell;
    }

    /// <summary>Copies every attribute of the attributed variable at
    /// <paramref name="fromAddr"/> onto the one at <paramref name="toAddr"/>.
    /// A module the destination lacks is added outright. A module both
    /// carry is resolved differently depending on whether the program
    /// defines a <c>verify_attributes/4</c> hook:
    /// <list type="bullet">
    /// <item>No hook — the hookless merge rule: the two values
    /// must unify, or the whole unification fails.</item>
    /// <item>Hook present — the destination keeps its own value and the
    /// hook (run from the queued wakeup) owns the merge. Pre-unifying
    /// here would fail before the hook could run, which is wrong for a
    /// constraint library whose variables carry different domains.</item>
    /// </list></summary>
    private bool MergeAttributes(int fromAddr, int toAddr)
    {
        int fromHome = Deref(fromAddr);
        int toHome = Deref(toAddr);
        if (_heap[fromHome].Tag != Tag.AttVar || _heap[toHome].Tag != Tag.AttVar)
            return true;
        var fromRecord = _attrTable[fromHome];
        var toRecord = _attrTable[toHome];
        // Snapshot the source modules: the Unify below can't mutate
        // fromRecord, but iterating a dictionary we may also be reading
        // is fragile — copy the pairs first.
        foreach (var (moduleId, fromValueIdx) in fromRecord.ToArray())
        {
            int toValueIdx = toRecord.TryGetValue(moduleId, out int v) ? v : -1;
            if (toValueIdx < 0)
            {
                TrailAttrChange(toHome, moduleId, -1);
                toRecord[moduleId] = fromValueIdx;
            }
            else if (ModuleHasHook(moduleId))
            {
                // The hook owns this shared module's merge — leave the
                // destination value untouched; the queued wakeup runs
                // verify_attributes/4, which reads both sides.
            }
            else if (!Unify(toValueIdx, fromValueIdx))
            {
                return false;
            }
        }
        return true;
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

}
