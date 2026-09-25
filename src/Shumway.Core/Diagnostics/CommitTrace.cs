using System.Diagnostics;

namespace Shumway.Core.Diagnostics;

/// <summary>Where alternatives are created, committed to, and gone back
/// to: choice-point pushes, cuts, and trail unwinds, with B before and
/// after.
///
/// <para>The cell trace showed the tier undoing twenty-five bindings the
/// interpreter never undoes, at the point the goal finishes. An undo is a
/// backtrack, a backtrack goes to a choice point, and a choice point is
/// still there only because nothing cut it away -- so the question moved
/// from "what was undone" to "what was not committed". This records the
/// three events that answer it.</para>
///
/// <para>A caveat that has to be read with the output, not discovered
/// afterwards: the wasm tier pushes and cuts INSIDE the module, writing B
/// in shared memory without calling anything here, so its own commits do
/// not appear. What does appear is every one the host performs, and the
/// host sees the tier's B because the chain syncs it. So a cut present on
/// one side and absent on the other is evidence about WHERE the commit
/// happened, not proof that it did not happen -- while a BACKTRACK is
/// recorded on both, because the extra trail cannot be unwound inside wasm
/// at all.</para></summary>
public static class CommitTrace
{
    private const string Symbol = "SHUMWAY_DIAG";
    private const int Capacity = 60_000;

    /// <summary>Trail records an APPEND to the extra trail, whose
    /// `from` is the TrailType: an unwind's target is a top, and a top
    /// only means something once you know what is under it.</summary>
    public enum Kind : byte { Push, Cut, Unwind, Trail }

    private static readonly Kind[] Kinds = new Kind[Capacity];
    private static readonly int[] Froms = new int[Capacity];
    private static readonly int[] Tos = new int[Capacity];
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

    /// <summary>For a push, <paramref name="from"/> is the previous B and
    /// <paramref name="to"/> the new one; for a cut, the B it left and the
    /// barrier it committed to; for an unwind, the trail tops it went back
    /// to.</summary>
    [Conditional(Symbol)]
    public static void Note(long cells, Kind kind, int from, int to)
    {
        if (!Enabled) return;
        Total++;
        if (_count == Capacity) return;
        Cells[_count] = cells;
        Kinds[_count] = kind;
        Froms[_count] = from;
        Tos[_count] = to;
        _count++;
    }

    /// <summary>One event per line: sequence, kind, from, to, cells.
    /// </summary>
    public static string Dump()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# seq kind from to cells (").Append(_count).Append(" of ")
          .Append(Total).Append(" events)\n");
        for (int i = 0; i < _count; i++)
            sb.Append(i).Append(' ').Append(Kinds[i]).Append(' ').Append(Froms[i])
              .Append(' ').Append(Tos[i]).Append(' ').Append(Cells[i]).Append('\n');
        return sb.ToString();
    }
}
