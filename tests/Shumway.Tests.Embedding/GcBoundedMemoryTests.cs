using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Bounded memory on long deterministic runs — the two retention root causes
/// the heap-gc-stack-roots arc found and fixed:
///
/// <list type="bullet">
/// <item>Dead choice points under LCO-reused frames: a clause selected via
///   try_me_else whose cut discards the CP left its slots below the frame
///   forever (~15 stack slots per iteration on a lazy DCG). Deallocate now
///   reclaims down to max(E-chain top, B-chain top).</item>
/// <item>Orphaned attr-trail-log records: a cut's trail compaction dropped
///   young AttrModify entries but left their side-log records, and the GC
///   rooted every record's Home/OldValue — a lazy phrase_from_file retained
///   its ENTIRE consumed input (one orphan per chunk). Dropped entries now
///   dead-mark their records.</item>
/// </list>
/// </summary>
public class GcBoundedMemoryTests : IDisposable
{
    private readonly string _tmp;

    public GcBoundedMemoryTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(),
            "shumway_gcbound_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    [Fact]
    public void DeterministicLcoLoop_KeepsTheControlStackFlat()
    {
        // count/1's clauses need a try/trust chain (0 vs var first args do not
        // discriminate for a var call pattern... they do for ints — force the
        // undiscriminated shape with a var head arg + guard), so every
        // iteration pushes a CP that the cut discards. 50k iterations must
        // not grow the stack by 50k dead CPs.
        var e = new PrologEngine();
        e.ConsultString(
            "dummy(_).\n"
            + "cnt(N, _) :- N =< 0, !.\n"
            + "cnt(N, Max) :- dummy(N), N1 is N - 1, cnt(N1, Max).\n"
            + "probe(T) :- '$stack_top'(T).\n");
        Assert.True(e.Query(
            "cnt(50000, m), probe(T), T < 2000.").Success);
    }

    [Fact]
    public void LazyPhraseFromFile_DoesNotRetainConsumedInput()
    {
        // ~240KB of newline-separated junk; the parse consumes it through
        // phrase_from_file's freeze-chunked lazy list. Retaining the consumed
        // input would keep ~80k+ cells live (240K chars, packed 3/cell);
        // bounded behaviour leaves only the tail machinery.
        string data = Path.Combine(_tmp, "data.txt").Replace('\\', '/');
        var line = new string('x', 59) + "\n";
        File.WriteAllText(data, string.Concat(Enumerable.Repeat(line, 4000)));

        var e = new PrologEngine();
        e.ConsultString(
            "lines(N0, N) --> line, !, { N1 is N0 + 1 }, lines(N1, N).\n"
            + "lines(N, N) --> [].\n"
            + "line --> ['\\n'], !.\n"
            + "line --> [_], line.\n");
        Assert.True(e.Query(
            $"phrase_from_file(lines(0, N), '{data}'), N =:= 4000, "
            + "'$heap_live'(Live, _, _), Live < 20000.").Success);
    }

    [Fact]
    public void CutCompaction_DoesNotOrphanAttrLogRecords()
    {
        // The direct shape of root cause 2: attribute mutations inside a
        // once/1 (its commit compacts the trail) must not leave GC-rooted
        // log records behind. 300 rounds of freeze + bind inside once, then
        // the frozen goals' terms must be collectable.
        var e = new PrologEngine();
        e.Query("use_module(library(coroutining)).");
        e.ConsultString(
            "big(_).\n"
            + "round(0) :- !.\n"
            + "round(N) :- once(( freeze(V, big(V)), V = [N|_] )), "
            + "N1 is N - 1, round(N1).\n");
        Assert.True(e.Query(
            "round(300), '$heap_live'(Live, _, _), Live < 5000.").Success);
    }
}
