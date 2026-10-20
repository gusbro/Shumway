namespace Shumway.Core;

/// <summary>ADR-063: a term's cells as the heap lays them, with every heap
/// index in them relative to the image's first cell. Immutable and shared by
/// every activation: <c>put_blit</c> copies it to the heap top and relocates
/// (<see cref="CopyTo"/>). A variable of the term is a REF cell to its own
/// offset (unbound once relocated) or to the offset of its first occurrence;
/// the <see cref="Holes"/> say which cells the copy fills from a register
/// and which registers it points at a cell. Equal by content, holes
/// included, so a literal pool interns one image per distinct term and
/// register assignment.</summary>
public sealed class TermImage : IEquatable<TermImage>
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
