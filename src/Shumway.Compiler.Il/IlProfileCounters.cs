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
/// <para>The IL embeds the profile key <see cref="Allocate"/> handed out
/// as a constant and calls <see cref="Bump"/>; the phase-2 recompile reads
/// the counts back with <see cref="Get"/>.</para>
/// </summary>
public static class IlProfileCounters
{
    // Bump runs at every success of an instrumented clause, in every engine:
    // it reads the table without a lock. Allocate and Release write under
    // the lock, and a grown table is published after its slots are filled.
    // A released key is handed out again, so the table holds the profiles
    // alive at once, not every one a long-running process ever took. An
    // instrumented delegate still running after its key was released may
    // bump the key's next owner: that perturbs a profile, never an answer.
    private static long[]?[] _byKey = new long[]?[16];
    private static readonly Stack<int> _free = new();
    private static int _next = 1;
    private static readonly object _lock = new();

    /// <summary>A fresh key with a zeroed counter of <paramref name="size"/>
    /// slots (one per clause). Called by the IL compiler when it emits an
    /// instrumented predicate.</summary>
    public static int Allocate(int size)
    {
        lock (_lock)
        {
            int key = _free.Count > 0 ? _free.Pop() : _next++;
            var table = _byKey;
            if (key >= table.Length)
            {
                var grown = new long[]?[Math.Max(table.Length * 2, key + 1)];
                Array.Copy(table, grown, table.Length);
                table = grown;
            }
            table[key] = new long[size];
            Volatile.Write(ref _byKey, table);
            return key;
        }
    }

    /// <summary>Records one success of clause <paramref name="clauseIndex"/>
    /// for the predicate registered under <paramref name="key"/>. The
    /// increment races harmlessly across engines sharing the process: a
    /// lost count only perturbs the profile, never correctness.</summary>
    public static void Bump(int key, int clauseIndex)
    {
        var table = Volatile.Read(ref _byKey);
        if ((uint)key < (uint)table.Length && table[key] is { } counters
            && (uint)clauseIndex < (uint)counters.Length)
        {
            counters[clauseIndex]++;
        }
    }

    /// <summary>Returns a snapshot copy of the counter array for
    /// <paramref name="key"/>, or <c>null</c> when nothing is
    /// registered. The phase-2 recompile uses this to order the
    /// dispatch.</summary>
    public static long[]? Get(int key)
    {
        lock (_lock)
        {
            var table = _byKey;
            return (uint)key < (uint)table.Length && table[key] is { } counters
                ? (long[])counters.Clone()
                : null;
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

    /// <summary>Drops a key's counters and frees the key — called once a
    /// predicate has been recompiled to its optimised form and the profile
    /// is no longer needed. Releasing a key twice is harmless.</summary>
    public static void Release(int key)
    {
        lock (_lock)
        {
            var table = _byKey;
            if ((uint)key < (uint)table.Length && table[key] is not null)
            {
                table[key] = null;
                _free.Push(key);
            }
        }
    }

    /// <summary>Diagnostic: the slots the key table holds.</summary>
    public static int TableLength => Volatile.Read(ref _byKey).Length;
}
