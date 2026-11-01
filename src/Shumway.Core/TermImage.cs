namespace Shumway.Core;

/// <summary>ADR-063: a term's cells as the heap lays them, with every heap
/// index in them relative to the image's first cell. Immutable and shared by
/// every activation: <c>put_blit</c> copies it to the heap top and relocates
/// (<see cref="CopyTo"/>). A variable of the term is a REF cell to its own
/// offset, unbound once relocated; every cell of a named one is among the
/// <see cref="Holes"/>, which say which cells the copy fills from a register
/// and which registers it points at a cell. Equal by content, holes
/// included, so a literal pool interns one image per distinct term and
/// register assignment.</summary>
public sealed class TermImage : IEquatable<TermImage>, IAtomHolder
{
    private readonly Cell[] _cells;
    private readonly ImageHole[] _holes;

    // What a payload gains per unit of base, by tag: an index in the low
    // bits (a REF, a STR, a LIS, a FLOAT header's pair) moves by one, a PSTR
    // header keeps its buffer index two bits up, and nothing else is an index.
    private static readonly long[] Step = BuildSteps();

    private static long[] BuildSteps()
    {
        var s = new long[16];
        s[(int)Tag.Ref] = 1;
        s[(int)Tag.Str] = 1;
        s[(int)Tag.Lis] = 1;
        s[(int)Tag.Float] = 1;
        s[(int)Tag.Pstr] = 1 << 2;
        return s;
    }

    /// <summary><paramref name="c"/> with the heap index in it, if any, moved by
    /// <paramref name="delta"/>.</summary>
    public static Cell Relocate(Cell c, long delta)
        => new(c.Data + delta * Step[(int)(c.Data >> Cell.TagShift) & 0xF]);

    public TermImage(Cell[] cells, Tag root) : this(cells, root, null) { }

    public TermImage(Cell[] cells, Tag root, ImageHole[]? holes)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Length == 0)
            throw new ArgumentException("An image has at least one cell.", nameof(cells));
        if (root is not (Tag.Str or Tag.Lis))
            throw new ArgumentOutOfRangeException(nameof(root), "An image is a structure or a list.");
        _cells = cells;
        _holes = holes ?? Array.Empty<ImageHole>();
        foreach (var h in _holes)
            if ((uint)h.Offset >= (uint)cells.Length)
                throw new ArgumentOutOfRangeException(nameof(holes), $"Hole at {h.Offset} is outside the image.");
        Root = root;
    }

    /// <summary>The tag of the cell that names the term: <see cref="Tag.Str"/>
    /// for a structure, <see cref="Tag.Lis"/> for a list.</summary>
    public Tag Root { get; }

    public int Length => _cells.Length;

    public ReadOnlySpan<Cell> Cells => _cells;

    /// <summary>The cells the copy fills from a register, and the registers
    /// it points at a cell, in the order they are applied.</summary>
    public ReadOnlySpan<ImageHole> Holes => _holes;

    public bool HasHoles => _holes.Length > 0;

    /// <summary>Copies the image into <paramref name="heap"/> at
    /// <paramref name="start"/>, every index in it relocated by that much.
    /// The caller has reserved <see cref="Length"/> cells there.</summary>
    public void CopyTo(Cell[] heap, int start)
    {
        var src = _cells;
        long b = start;
        for (int i = 0; i < src.Length; i++)
        {
            long d = src[i].Data;
            heap[start + i] = new Cell(d + b * Step[(int)(d >> Cell.TagShift) & 0xF]);
        }
    }

    /// <summary>The cell that names the copy at <paramref name="start"/>.</summary>
    public Cell RootCell(int start) => Root == Tag.Lis ? Cell.Lis(start) : Cell.Str(start);

    /// <summary>Adds the atoms the cells name (ADR-003): an atom cell, a
    /// functor's name.</summary>
    public void MarkAtoms(AtomMarkSet set)
    {
        var cells = _cells;
        for (int i = 0; i < cells.Length; i++) set.AddCell(cells[i]);
    }

    private int[]? _transientAtomIds;

    /// <summary>The transient atoms the cells name, ascending and distinct
    /// (ADR-003): what a holder of the image claims. Computed once.</summary>
    public int[] TransientAtomIds => _transientAtomIds ??= ComputeTransientAtomIds();

    private int[] ComputeTransientAtomIds()
    {
        List<int>? ids = null;
        foreach (var cell in _cells)
        {
            int id = cell.Tag == Tag.Atom ? cell.AsAtomId
                : cell.Tag == Tag.Functor && FunctorTable.TryLookup(cell.AsFunctorId, out var f) ? f.AtomId
                : -1;
            if (id >= 0 && AtomTable.IsTransient(id)) (ids ??= new()).Add(id);
        }
        if (ids is null) return Array.Empty<int>();
        ids.Sort();
        int n = 0;
        for (int i = 0; i < ids.Count; i++)
            if (n == 0 || ids[i] != ids[n - 1]) ids[n++] = ids[i];
        ids.RemoveRange(n, ids.Count - n);
        return ids.ToArray();
    }

    /// <summary>True when the term at heap cell <paramref name="heapIdx"/>
    /// certainly does not unify with the image's term: they differ in an atom,
    /// an integer, a functor or a list cell within <paramref name="depth"/>
    /// levels. False when they may; a variable, a float or a packed string on
    /// either side is a may.</summary>
    public bool DefinitelyDiffers(Activation engine, int heapIdx, int depth)
        => Differs(engine, heapIdx, RootCell(0), depth);

    private bool Differs(Activation engine, int heapIdx, Cell b, int depth)
    {
        if (depth <= 0) return false;
        int idx = engine.Deref(heapIdx);
        Cell h = engine.GetHeap(idx);
        switch (h.Tag)
        {
            case Tag.Atom:
                return b.Tag switch
                {
                    Tag.Atom => b.AsAtomId != h.AsAtomId,
                    Tag.Int or Tag.Str or Tag.Lis => true,
                    _ => false,
                };
            case Tag.Int:
                return b.Tag switch
                {
                    Tag.Int => b.AsInt != h.AsInt,
                    Tag.Atom or Tag.Str or Tag.Lis => true,
                    _ => false,
                };
            case Tag.Str:
            {
                if (b.Tag is Tag.Atom or Tag.Int) return true;
                if (b.Tag != Tag.Str) return false;
                Cell f = engine.GetHeap(h.AsHeapIndex);
                Cell bf = _cells[b.AsHeapIndex];
                if (f.Tag != Tag.Functor || bf.Tag != Tag.Functor) return false;
                if (f.AsFunctorId != bf.AsFunctorId) return true;
                int arity = FunctorTable.Lookup(f.AsFunctorId).Arity;
                for (int k = 1; k <= arity; k++)
                    if (Differs(engine, h.AsHeapIndex + k, _cells[b.AsHeapIndex + k], depth - 1))
                        return true;
                return false;
            }
            case Tag.Lis:
                if (b.Tag is Tag.Atom or Tag.Int) return true;
                if (b.Tag != Tag.Lis) return false;
                return Differs(engine, h.AsHeapIndex, _cells[b.AsHeapIndex], depth - 1)
                    || Differs(engine, h.AsHeapIndex + 1, _cells[b.AsHeapIndex + 1], depth - 1);
            default:
                return false;
        }
    }

    /// <summary>Copies the cells from <paramref name="offset"/> to
    /// <paramref name="end"/> into <paramref name="heap"/> at
    /// <paramref name="start"/>, every index in them relocated so that the
    /// block's own cells land where they are copied. The caller has reserved
    /// the cells there.</summary>
    public void CopyBlockTo(Cell[] heap, int start, int offset, int end)
    {
        var src = _cells;
        long b = start - offset;
        for (int i = offset; i < end; i++)
        {
            long d = src[i].Data;
            heap[start + i - offset] = new Cell(d + b * Step[(int)(d >> Cell.TagShift) & 0xF]);
        }
    }

    private int[]? _blockEnd;
    private int[]? _blockArity;
    private bool[]? _blockBoxed;

    /// <summary>The end of the sub-term whose block (a structure's functor and
    /// arguments, a list's pair) starts at <paramref name="offset"/>: the
    /// builder lays a sub-term as one run, its literals and nested blocks
    /// after its own cells, so the cells from the offset to the end are the
    /// sub-term and nothing else.</summary>
    public int BlockEnd(int offset) => (_blockEnd ?? ComputeBlockEnds())[offset];

    /// <summary>By block start: the number of argument cells of the block
    /// (two for a list's pair).</summary>
    public ReadOnlySpan<int> BlockArities
    {
        get
        {
            if (_blockArity is null) ComputeBlockEnds();
            return _blockArity!;
        }
    }

    /// <summary>Whether an argument of the block at <paramref name="offset"/>
    /// is a boxed literal, a float or a packed string: a matching stream
    /// takes such a compound apart from its parent's, never inline.</summary>
    public bool BlockHasBoxedArgument(int offset)
    {
        if (_blockBoxed is null) ComputeBlockEnds();
        return _blockBoxed![offset];
    }

    // Plain arrays, no delegate: the IL tier's helper analysis descends
    // into this code. Each block is laid once, so a block count bounds the
    // stack and the order.
    private int[] ComputeBlockEnds()
    {
        var cells = _cells;
        int n = cells.Length;
        var ends = new int[n];
        var arity = new int[n];
        var boxed = new bool[n];
        var order = new int[n];     // the blocks as found, a parent before its children
        var isList = new bool[n];
        var stack = new int[n];
        int count = 0, sp = 0;
        stack[sp++] = 0;
        isList[0] = Root == Tag.Lis;
        while (sp > 0)
        {
            int bo = stack[--sp];
            order[count++] = bo;
            int first = isList[bo] ? bo : bo + 1;
            int k = isList[bo] ? 2 : FunctorTable.Lookup(cells[bo].AsFunctorId).Arity;
            arity[bo] = k;
            for (int i = 0; i < k; i++)
            {
                Cell c = cells[first + i];
                if (c.Tag is Tag.Str or Tag.Lis)
                {
                    int sub = c.AsHeapIndex;
                    isList[sub] = c.Tag == Tag.Lis;
                    stack[sp++] = sub;
                }
                else if (c.Tag == Tag.Ref && cells[c.AsHeapIndex].Tag is Tag.Float or Tag.Pstr)
                {
                    boxed[bo] = true;
                }
            }
        }
        for (int j = count - 1; j >= 0; j--)   // the children before their parent
        {
            int bo = order[j];
            int first = isList[bo] ? bo : bo + 1;
            int k = arity[bo];
            int end = first + k;
            for (int i = 0; i < k; i++)
            {
                Cell c = cells[first + i];
                int e = 0;
                if (c.Tag is Tag.Str or Tag.Lis)
                {
                    e = ends[c.AsHeapIndex];
                }
                else if (c.Tag == Tag.Ref)
                {
                    int p = c.AsHeapIndex;
                    Cell t = cells[p];
                    if (t.Tag == Tag.Float) e = p + 2;
                    else if (t.Tag == Tag.Pstr) e = PstrBlockEnd(p, t);
                }
                if (e > end) end = e;
            }
            ends[bo] = end;
        }
        _blockArity = arity;
        _blockBoxed = boxed;
        _blockEnd = ends;
        return ends;
    }

    /// <summary>The end of a packed string's block: the header at
    /// <paramref name="header"/>, its buffer cells, the tail cell.</summary>
    public static int PstrBlockEnd(int header, Cell headerCell)
        => header + 2 + (headerCell.AsPstrLength + Cell.PstrCodeUnitsPerBuffer - 1) / Cell.PstrCodeUnitsPerBuffer;

    /// <summary>The code units of the packed string whose header is at
    /// <paramref name="header"/>, this segment of it.</summary>
    public string PstrRun(int header)
    {
        var cells = _cells;
        int n = cells[header].AsPstrLength;
        var run = new char[n];
        for (int i = 0; i < n; i++)
            run[i] = (char)cells[header + 1 + i / Cell.PstrCodeUnitsPerBuffer]
                .AsPstrCodeUnit(i % Cell.PstrCodeUnitsPerBuffer);
        return new string(run);
    }

    /// <summary>The cell that holds the tail of the packed string whose header
    /// is at <paramref name="header"/>: <c>[]</c>, a REF to the next segment's
    /// header, or anything a list's tail can be.</summary>
    public int PstrTailOffset(int header) => PstrBlockEnd(header, _cells[header]) - 1;

    public bool Equals(TermImage? other)
        => other is not null && Root == other.Root && _cells.AsSpan().SequenceEqual(other._cells)
            && _holes.AsSpan().SequenceEqual(other._holes);

    public override bool Equals(object? obj) => Equals(obj as TermImage);

    public override int GetHashCode()
    {
        // Not System.HashCode: .NET Framework has no such type.
        unchecked
        {
            long h = (long)Root * unchecked((long)0x9E3779B97F4A7C15UL) + _cells.Length;
            for (int i = 0; i < _cells.Length; i++)
                h = (h ^ _cells[i].Data) * 0x100000001B3L;
            for (int i = 0; i < _holes.Length; i++)
                h = (h ^ _holes[i].Key) * 0x100000001B3L;
            return (int)h ^ (int)(h >> 32);
        }
    }
}
