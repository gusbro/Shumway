namespace Shumway.Core;

/// <summary>ADR-063: a ground term's cells as the heap lays them, with every
/// heap index in them relative to the image's first cell. Immutable and
/// shared by every activation: <c>put_blit</c> copies it to the heap top and
/// relocates (<see cref="CopyTo"/>). Equal by content, so a literal pool
/// interns one image per distinct term.</summary>
public sealed class TermImage : IEquatable<TermImage>
{
    private readonly Cell[] _cells;

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

    public TermImage(Cell[] cells, Tag root)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Length == 0)
            throw new ArgumentException("An image has at least one cell.", nameof(cells));
        if (root is not (Tag.Str or Tag.Lis))
            throw new ArgumentOutOfRangeException(nameof(root), "An image is a structure or a list.");
        _cells = cells;
        Root = root;
    }

    /// <summary>The tag of the cell that names the term: <see cref="Tag.Str"/>
    /// for a structure, <see cref="Tag.Lis"/> for a list.</summary>
    public Tag Root { get; }

    public int Length => _cells.Length;

    public ReadOnlySpan<Cell> Cells => _cells;

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
        => other is not null && Root == other.Root && _cells.AsSpan().SequenceEqual(other._cells);

    public override bool Equals(object? obj) => Equals(obj as TermImage);

    public override int GetHashCode()
    {
        // Not System.HashCode: .NET Framework has no such type.
        unchecked
        {
            long h = (long)Root * unchecked((long)0x9E3779B97F4A7C15UL) + _cells.Length;
            for (int i = 0; i < _cells.Length; i++)
                h = (h ^ _cells[i].Data) * 0x100000001B3L;
            return (int)h ^ (int)(h >> 32);
        }
    }
}
