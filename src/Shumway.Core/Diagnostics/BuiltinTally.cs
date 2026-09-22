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
        lock (Counts) Counts.Clear();
    }

    [Conditional(Symbol)]
    public static void Note(int builtinId)
    {
        if (!Enabled) return;
        lock (Counts)
        {
            Counts.TryGetValue(builtinId, out long n);
            Counts[builtinId] = n + 1;
        }
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
