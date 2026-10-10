using Shumway.Core.Diagnostics;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A cut's trail compaction walks from the last walk's mark when
/// the cut is to the same parent and the trail only grew since, not from
/// the parent's tops. A backtrackable global write survives every cut, so
/// a loop of write-then-cut used to re-read every earlier write on every
/// cut: quadratic, and exactly what clp(Z)'s propagation does (two writes
/// per propagator, a cut per if-then-else). Measured in the browser, 20k
/// iterations took 5.6 s on Tier-0 and 6.4 s on the tier.</summary>
public sealed class IncrementalTrailCompactionTests(ITestOutputHelper o)
{
    // member/2 leaves a choice point that the cut removes, so every
    // iteration compacts; b_setval's entry survives each time.
    private const string Corpus = """
        loop(0) :- !.
        loop(N) :- b_setval(k, N), member(_, [a, b]), !, N1 is N - 1, loop(N1).
        after(N, V) :- loop(N), b_getval(k, V).
        undone(N) :- ( loop(N), fail ; \+ '$fetch_global_var'(k, _) ).
        """;

    /// <summary>The count: entries visited by the walks grow with N, not
    /// with N squared. A quadratic walk reads about N*N/2 entries; the
    /// doubled loop would then visit four times as many.</summary>
    [DiagFact]
    public void TheWalksReadEachEntryOnce()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Corpus);
        long Visited(int n)
        {
            CompactCensus.Reset();
            Assert.True(engine.Query($"after({n}, V), V == 1.").Success);
            o.WriteLine($"n={n}: walks={CompactCensus.Walks} visited={CompactCensus.Visited}");
            return CompactCensus.Visited;
        }
        long small = Visited(200), large = Visited(400);
        Assert.True(CompactCensus.Walks >= 400, "the corpus stopped cutting");
        Assert.True(small > 0, "the walks read nothing: the corpus stopped trailing");
        Assert.True(large < 3 * small,
            $"visited {small} entries for 200 iterations and {large} for 400: the walk is quadratic again");
    }

    /// <summary>The answers, on both tiers: the last write is what a later
    /// read sees, and backtracking out of the loop undoes every write --
    /// the entries a late start keeps are the ones that were kept anyway.
    /// </summary>
    [Fact]
    public void TheEntriesStillUnwind()
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);
        var (tiered, _) = TieredEngine.Build(Corpus);
        foreach (var (e, name) in new[] { (plain, "Tier-0"), (tiered, "the tier") })
        {
            Assert.True(e.Query("after(300, V), V == 1.").Success, name);
            Assert.True(e.Query("undone(300).").Success, $"{name}: a write survived the backtrack");
        }
    }
}
