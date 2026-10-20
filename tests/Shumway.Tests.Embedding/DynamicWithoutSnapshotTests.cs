using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A dynamic predicate with no snapshot this query (no clauses, or
/// mutated since the query's setup) is asked of the promotion store once
/// past the threshold, not at every call: the query's dispatch caches the
/// answer, and the next query asks again. A parser asks its empty pushback
/// predicate at every character.</summary>
public sealed class DynamicWithoutSnapshotTests
{
    private const string Program = """
        :- dynamic pushback/1.
        scan(0) :- !.
        scan(N) :- ( pushback(_) -> true ; true ), N1 is N - 1, scan(N1).
        """;

    private const int Calls = 5000;

    // The store's invocation count of the predicate: how many calls reached
    // it past the dispatch cache.
    private static int StoreCount(PrologEngine e, string indicator)
    {
        foreach (string raw in e.IlPromotion.DescribePromotionState().Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("candidate ") || !line.Contains($"{indicator}:")) continue;
            int at = line.IndexOf("count=") + "count=".Length;
            int end = line.IndexOf(' ', at);
            return int.Parse(line[at..(end < 0 ? line.Length : end)]);
        }
        return 0;
    }

    private static bool Promoted(PrologEngine e, string indicator)
    {
        foreach (int fid in e.IlPromotion.PromotedFunctorIds())
        {
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
            string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
            if ($"{name[(name.LastIndexOf('$') + 1)..]}/{arity}" == indicator) return true;
        }
        return false;
    }

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(Program);
        return e;
    }

    [Fact]
    public void AnEmptyDynamicIsAskedOncePastTheThresholdInAQuery()
    {
        var e = Engine();
        Assert.True(e.Query($"scan({Calls}).").Success);
        int count = StoreCount(e, "pushback/1");
        Assert.True(count >= 1, "the calls never reached the store");
        Assert.True(count <= e.IlPromotion.Threshold,
            $"the store was asked {count} times for an empty predicate in one query");
        Assert.False(Promoted(e, "pushback/1"));
    }

    [Fact]
    public void ADynamicMutatedInTheQueryIsAskedOnceAndPromotesInTheNext()
    {
        var e = Engine();
        // The assertz makes it stale for the rest of this query.
        Assert.True(e.Query($"assertz(pushback(a)), scan({Calls}).").Success);
        Assert.True(StoreCount(e, "pushback/1") <= e.IlPromotion.Threshold);
        Assert.False(Promoted(e, "pushback/1"));
        // The next query's setup sees the clause: the cached answer was the
        // query's, not the store's (ANTI-VACUITY for the cache's lifetime).
        Assert.True(e.Query($"scan({Calls}).").Success);
        Assert.True(Promoted(e, "pushback/1"), e.IlPromotion.DescribePromotionState());
    }

    [Fact]
    public void TheCachedAnswerIsTheDispatchsNotTheDatabases()
    {
        var e = Engine();
        // Hot and empty, then asserted, then called: the live clause chain
        // answers (the logical update view), whatever the dispatch cached.
        var r = e.Query($"scan({Calls}), assertz(pushback(y)), pushback(Z).");
        Assert.True(r.Success);
        Assert.Equal("y", r.Bindings["Z"].ToString());
        Assert.True(e.Query($"scan(10), pushback(W), W == y.").Success);
    }
}
