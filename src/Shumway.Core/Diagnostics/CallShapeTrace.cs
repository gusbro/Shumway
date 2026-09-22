using System.Diagnostics;

namespace Shumway.Core.Diagnostics;

/// <summary>What a builtin is being HANDED, entry by entry, so two runs of
/// the same goal can be diffed until the first call that differs.
///
/// <para>A tally says a builtin ran two million times on one tier and
/// eighty-one on the other. It cannot say whether the tier is walking a
/// bigger term, the same term repeatedly, or a term that grows as it is
/// walked -- three different faults with the same count. The argument's
/// shape separates them: a walk that descends shows shrinking arities, one
/// that re-feeds itself shows the same arity forever, and one that builds
/// shows it climbing.</para>
///
/// <para>Recorded per entry: the functor NAME id and arity for a compound,
/// or a negative tag code otherwise, plus the cells allocated so far so an
/// entry can be lined up against the area trace. Names, not heap indices:
/// indices differ between two runs for reasons that have nothing to do with
/// the bug.</para>
///
/// <para>It stops at the cap rather than wrapping -- the question is where
/// the two runs FIRST differ -- and <see cref="Wants"/> lets a call site
/// skip the work of describing an argument nobody will record.</para>
/// </summary>
public static class CallShapeTrace
{
    private const string Symbol = "SHUMWAY_DIAG";
    private const int Capacity = 40_000;

    private static readonly int[] Names = new int[Capacity];
    private static readonly int[] Arities = new int[Capacity];
    private static readonly long[] Cells = new long[Capacity];
    private static int _count;

    /// <summary>Off by default: asked for around one goal, never left on.
    /// </summary>
    public static bool Enabled;

    /// <summary>How many calls happened, including any past the cap.
    /// </summary>
    public static long Total;

    /// <summary>Whether describing an argument is worth the walk.</summary>
    public static bool Wants => Enabled && _count < Capacity;

    [Conditional(Symbol)]
    public static void Reset()
    {
        _count = 0;
        Total = 0;
    }

    [Conditional(Symbol)]
    public static void Note(long cells, int nameAtomId, int arity)
    {
        if (!Enabled) return;
        Total++;
        if (_count == Capacity) return;
        Cells[_count] = cells;
        Names[_count] = nameAtomId;
        Arities[_count] = arity;
        _count++;
    }

    /// <summary>One call per line: sequence, functor, arity, cells.</summary>
    public static string Dump()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# seq functor arity cells (").Append(_count).Append(" of ")
          .Append(Total).Append(" calls)\n");
        for (int i = 0; i < _count; i++)
        {
            // "tag", not a dash: a dash is also a real functor name, and a
            // placeholder that can be mistaken for one turns a diff into a
            // misreading. (It did.)
            string n;
            if (Arities[i] < 0) n = "tag";
            else
            {
                try { n = AtomTable.GetById(Names[i])?.Name ?? Names[i].ToString(); }
                catch (System.Exception) { n = Names[i].ToString(); }
            }
            sb.Append(i).Append(' ').Append(n).Append(' ').Append(Arities[i])
              .Append(' ').Append(Cells[i]).Append('\n');
        }
        return sb.ToString();
    }
}
