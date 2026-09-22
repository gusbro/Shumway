using System.Collections.Generic;
using System.Diagnostics;

namespace Shumway.Core.Diagnostics;

/// <summary>Builtin invocations by id, counted the same way on both tiers.
///
/// <para>The tier already tallies the builtins it LEAVES THE CHAIN for, and
/// that number answers a tier question: which builtin costs a crossing. It
/// cannot answer a comparison, because the interpreter has no chain to
/// leave and the tier open-codes some builtins outright -- so a tier tally
/// and an interpreter one would be counting different events under the same
/// name, which is worse than having neither.</para>
///
/// <para>This counts the one event both reach: the implementation actually
/// running. Bumped at every site that invokes one, in the interpreter and
/// in the tier's chain loop alike, so the same goal on the two tiers
/// produces two numbers that mean the same thing and can be subtracted.
/// </para></summary>
public static class BuiltinTally
{
    private const string Symbol = "SHUMWAY_DIAG";

    private static readonly Dictionary<int, long> Counts = new();

    /// <summary>Off by default: asked for around one goal, never left on.
    /// </summary>
    public static bool Enabled;

    [Conditional(Symbol)]
    public static void Reset()
    {
        lock (Counts) { Counts.Clear(); _seq = 0; }
    }

    /// <summary>The same calls IN ORDER, with the cells allocated at each.
    /// A tally says how many; only the sequence says where two runs first
    /// take different steps. Bounded, and it stops rather than wrapping,
    /// because the beginning is the part being compared.</summary>
    private const int SeqCapacity = 60_000;
    private static readonly int[] SeqIds = new int[SeqCapacity];
    private static readonly long[] SeqCells = new long[SeqCapacity];
    private static int _seq;

    [Conditional(Symbol)]
    public static void Note(int builtinId, long cells)
    {
        if (!Enabled) return;
        lock (Counts)
        {
            Counts.TryGetValue(builtinId, out long n);
            Counts[builtinId] = n + 1;
            if (_seq < SeqCapacity)
            {
                SeqIds[_seq] = builtinId;
                SeqCells[_seq] = cells;
                _seq++;
            }
        }
    }

    /// <summary>(builtin id, cells) in call order.</summary>
    public static List<(int Id, long Cells)> Sequence()
    {
        var r = new List<(int, long)>();
        lock (Counts)
            for (int i = 0; i < _seq; i++) r.Add((SeqIds[i], SeqCells[i]));
        return r;
    }

    /// <summary>(builtin id, calls), heaviest first. Ids, not names: the
    /// registry lives above this assembly, so the caller resolves them.
    /// </summary>
    public static List<(int Id, long Calls)> Snapshot()
    {
        var r = new List<(int, long)>();
        lock (Counts)
            foreach (var kv in Counts) r.Add((kv.Key, kv.Value));
        r.Sort((x, y) => y.Item2.CompareTo(x.Item2));
        return r;
    }
}
