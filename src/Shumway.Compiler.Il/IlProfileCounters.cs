namespace Shumway.Compiler.Il;

/// <summary>
/// Profile-guided optimisation (PGO) of IL code: a
/// process-wide store of per-predicate dispatch-hit counters.
///
/// <para>When a multi-clause predicate is first promoted to Tier-1 IL,
/// it's compiled in <em>instrumented</em> form: each clause's success
/// path calls <see cref="Bump"/> to record that that clause produced
/// the answer. Once enough samples accumulate, the predicate is
/// recompiled in <em>optimised</em> form — the dispatch chain
/// reordered so the clause that succeeds most often is checked first
/// — and the instrumentation is dropped.</para>
///
/// <para>The counters are keyed by an integer profile key the IL
/// compiler allocates per promoted predicate (the same allocation
/// scheme as <see cref="IlPredicateCompiler.IndexedDelegateHolder"/>'s
/// holder keys). The IL embeds the key as a constant and calls
/// <see cref="Bump"/>; the phase-2 recompile reads the array back with
/// <see cref="Get"/>.</para>
/// </summary>
public static class IlProfileCounters
{
    private static readonly Dictionary<int, long[]> _byKey = new();
    private static readonly object _lock = new();

    /// <summary>Allocates a fresh counter array of <paramref name="size"/>
    /// slots (one per clause) under <paramref name="key"/>. Called by the
    /// IL compiler when it emits an instrumented predicate.</summary>
    public static void Allocate(int key, int size)
    {
        lock (_lock) _byKey[key] = new long[size];
    }

    /// <summary>Records one success of clause <paramref name="clauseIndex"/>
    /// for the predicate registered under <paramref name="key"/>. Called
    /// from instrumented IL on each clause's success path. The increment
    /// races harmlessly across engines sharing the process — a lost or
    /// torn count only perturbs the profile slightly, never correctness,
    /// so no per-bump lock is taken.</summary>
    public static void Bump(int key, int clauseIndex)
    {
        long[]? counters;
        lock (_lock) _byKey.TryGetValue(key, out counters);
        if (counters is not null
            && (uint)clauseIndex < (uint)counters.Length)
        {
            counters[clauseIndex]++;
        }
    }

    /// <summary>Returns a snapshot copy of the counter array for
    /// <paramref name="key"/>, or <c>null</c> when nothing was
    /// registered. The phase-2 recompile uses this to order the
    /// dispatch.</summary>
    public static long[]? Get(int key)
    {
        lock (_lock)
        {
            if (!_byKey.TryGetValue(key, out var counters)) return null;
            return (long[])counters.Clone();
        }
    }

    /// <summary>Total recorded samples for a key — the phase-2 trigger
    /// uses this to decide whether enough data has accumulated.</summary>
    public static long TotalSamples(int key)
    {
        long[]? counters = Get(key);
        if (counters is null) return 0;
        long total = 0;
        foreach (long c in counters) total += c;
        return total;
    }

    /// <summary>Drops a key's counters — called once a predicate has
    /// been recompiled to its optimised form and the profile is no
    /// longer needed.</summary>
    public static void Release(int key)
    {
        lock (_lock) _byKey.Remove(key);
    }
}
