using System.Diagnostics;

namespace Shumway.Core.Diagnostics;

/// <summary>The life of an attributed variable CELL: when one comes into
/// being, when a write takes it away, and when an undo puts it back.
///
/// <para>Every other trace here follows calls, and calls are where the two
/// tiers were shown to agree exactly -- same builtins, same order, same
/// allocation -- while still ending up with six cells attributed on one
/// side and not the other. A cell changes that way through BINDING and
/// through the undo of binding, which is compiled code on one tier and the
/// interpreter on the other and is not a call at all. That is the gap this
/// closes.</para>
///
/// <para>Three points, which between them are the whole lifecycle: the
/// record's creation (a plain variable becomes attributed), the trail entry
/// that precedes overwriting an attributed cell (it stops being one), and
/// the unwind that restores such a cell (it becomes one again). Addresses
/// are heap indices and are comparable between the two runs here, because
/// up to the divergence both allocate identically -- which the other traces
/// established and is what makes this one readable.</para></summary>
public static class AttVarCellTrace
{
    private const string Symbol = "SHUMWAY_DIAG";
    private const int Capacity = 60_000;

    /// <summary>What happened to the cell.</summary>
    public enum Kind : byte { Created, Overwritten, Restored }

    private static readonly int[] Addrs = new int[Capacity];
    private static readonly Kind[] Kinds = new Kind[Capacity];
    private static readonly long[] Cells = new long[Capacity];
    private static int _count;

    /// <summary>Off by default: asked for around one goal, never left on.
    /// </summary>
    public static bool Enabled;

    /// <summary>Events including any past the cap.</summary>
    public static long Total;

    [Conditional(Symbol)]
    public static void Reset()
    {
        _count = 0;
        Total = 0;
    }

    [Conditional(Symbol)]
    public static void Note(long cells, int addr, Kind kind)
    {
        if (!Enabled) return;
        Total++;
        if (_count == Capacity) return;
        Cells[_count] = cells;
        Addrs[_count] = addr;
        Kinds[_count] = kind;
        _count++;
    }

    /// <summary>One event per line: sequence, kind, address, cells.</summary>
    public static string Dump()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# seq kind addr cells (").Append(_count).Append(" of ")
          .Append(Total).Append(" events)\n");
        for (int i = 0; i < _count; i++)
            sb.Append(i).Append(' ').Append(Kinds[i]).Append(' ').Append(Addrs[i])
              .Append(' ').Append(Cells[i]).Append('\n');
        return sb.ToString();
    }
}
