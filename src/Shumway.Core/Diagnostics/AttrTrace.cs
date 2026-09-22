using System.Diagnostics;

namespace Shumway.Core.Diagnostics;

/// <summary>A real execution trace of the one thing constraint propagation
/// IS: every write of an attribute value, in order.
///
/// <para>It exists to be taken on both tiers and diffed until the first line
/// that differs. Most candidate traces cannot do that. A trace of builtin
/// calls looks different from the first entry, because the tier open-codes
/// the very builtins a solver leans on and the interpreter does not, so the
/// two streams disagree for a reason that has nothing to do with the bug. A
/// trace of goals cannot be taken at all on the tier, whose goals run inside
/// wasm and never surface. Attribute writes go through ONE funnel
/// (<c>AttrSet</c>) that both tiers reach, because the value lives in a side
/// table managed code owns.</para>
///
/// <para>What each entry records is chosen to survive the comparison. Heap
/// indices and variable identities differ between two runs for uninteresting
/// reasons, so they are not the point; the domain's SHAPE is. ADR-051 makes
/// a finite domain a <c>'$fd_dom'</c> term whose arity counts its intervals,
/// so the arity alone says how narrow the domain just became -- and a solver
/// that prunes less writes wider domains, which is exactly the difference
/// being looked for.</para>
///
/// <para>Bounded, and it stops rather than wrapping: the question is where
/// the two runs FIRST diverge, so the beginning is the part worth keeping
/// and the end is not.</para></summary>
public static class AttrTrace
{
    private const string Symbol = "SHUMWAY_DIAG";
    private const int Capacity = 120_000;

    private static readonly int[] Modules = new int[Capacity];
    private static readonly int[] Shapes = new int[Capacity];
    private static readonly long[] Cells = new long[Capacity];
    private static int _count;

    /// <summary>Off by default: a trace is asked for, never left running.
    /// </summary>
    public static bool Enabled;

    /// <summary>Which attributed variables were live when the engine
    /// last counted, with the modules each carries. Set by the builtin
    /// that does the counting; null until it runs.</summary>
    public static string? LiveSnapshot;

    /// <summary>How many writes happened, including any past the cap.
    /// </summary>
    public static long Total;

    [Conditional(Symbol)]
    public static void Reset()
    {
        _count = 0;
        Total = 0;
        LiveSnapshot = null;
    }

    /// <summary><paramref name="shape"/> is the value's functor arity for a
    /// compound, or a negative tag code for anything else.</summary>
    [Conditional(Symbol)]
    public static void Note(long cells, int moduleId, int shape)
    {
        if (!Enabled) return;
        Total++;
        if (_count == Capacity) return;
        Cells[_count] = cells;
        Modules[_count] = moduleId;
        Shapes[_count] = shape;
        _count++;
    }

    /// <summary>One write per line: sequence, module, domain shape, cells.
    /// The sequence number leads so a diff points straight at the write that
    /// first differs.</summary>
    public static string Dump()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# seq module shape cells (").Append(_count).Append(" of ")
          .Append(Total).Append(" writes)\n");
        for (int i = 0; i < _count; i++)
        {
            string m;
            try { m = AtomTable.GetById(Modules[i])?.Name ?? Modules[i].ToString(); }
            catch (System.Exception) { m = Modules[i].ToString(); }
            sb.Append(i).Append(' ').Append(m).Append(' ').Append(Shapes[i])
              .Append(' ').Append(Cells[i]).Append('\n');
        }
        return sb.ToString();
    }
}
