using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The literal pools are released with the code: consulted code
/// pins its literals, an asserted clause's go when the chain reclaim
/// bypasses its chunk or its buffer dies, a query's with the query, and a
/// freed id is reused with every copy of the pool refreshed first.</summary>
public sealed class LiteralPoolLifetimeTests
{
    // A term asserted from the heap carries its packed text, floats and big
    // integers as pool literals. The churn asserts and retracts a distinct
    // float each time.
    private const string Churn = """
        :- dynamic item/1, other/1, seen/1, fresh/1.
        churn(N) :- between(1, N, I), F is I + 0.5, assertz(other(F)), retract(other(F)), fail.
        churn(_).
        """;

    private static PrologEngine Tier0(string program)
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(program);
        return engine;
    }

    private static int IdOf(LiteralPool<TextLiteral> pool, string text)
    {
        for (int i = 0; i < pool.Count; i++)
            if (!pool.IsFree(i) && pool.Items[i].Text == text) return i;
        return -1;
    }

    [Fact]
    public void AChurnOfDistinctFloatsInsideOneQueryStaysBounded()
    {
        var engine = Tier0(Churn);
        var floats = engine._literalPools.Floats;
        Assert.True(engine.Query("churn(10).").Success);
        int baseline = floats.Count;
        Assert.True(engine.Query("churn(3000).").Success);
        // The sweep runs when the zeroed entries are a share of the pool, so
        // the pool settles a little above what the program itself names.
        Assert.True(floats.Count < baseline + baseline / 4 + 80,
            $"pool grew from {baseline} to {floats.Count} over 3000 distinct floats");
        Assert.True(floats.FreeCount > 0 || floats.PendingCount > 0, "no float was ever released");
        Assert.True(engine.Query("churn(3000).").Success);
        Assert.True(floats.Count < baseline + baseline / 4 + 80,
            $"pool grew from {baseline} to {floats.Count} over 6000 distinct floats");
    }

    [Fact]
    public void AChurnAcrossQueriesIsBoundedByTheRebuilds()
    {
        var engine = Tier0(Churn);
        var strings = engine._literalPools.Strings;
        var floats = engine._literalPools.Floats;
        int baseline = strings.Count, floatBaseline = floats.Count;
        // Each query's text names a string and a float of its own: the
        // query's units; the asserted clause's float, its chunk's.
        for (int i = 0; i < 1500; i++)
        {
            Assert.True(engine.Query($"assertz(item({i}.25)), X = \"q{i}\".").Success);
            Assert.True(engine.Query($"retract(item({i}.25)).").Success);
        }
        Assert.True(strings.Count < baseline + baseline / 4 + 80,
            $"pool grew from {baseline} to {strings.Count} over 1500 query strings");
        Assert.True(floats.Count < floatBaseline + floatBaseline / 4 + 80,
            $"pool grew from {floatBaseline} to {floats.Count} over 1500 asserted and retracted floats");
    }

    [Fact]
    public void AClauseAssertedOverAReusedIdReadsItsOwnValue()
    {
        var engine = Tier0(Churn);
        var floats = engine._literalPools.Floats;
        Assert.True(engine.Query("churn(2000).").Success);
        int slots = floats.Count;
        Assert.True(floats.FreeCount > 0, "no freed slot to reuse");
        // Inside one query: the assert reuses a freed id, and the clause runs
        // on the refreshed copy of the pool.
        Assert.True(engine.Query("churn(500), assertz(fresh(9.125)), fresh(X), X == 9.125.").Success);
        Assert.True(floats.TryGetId(9.125, out int id));
        Assert.True(id < slots, $"9.125 got id {id}, not a reused one under {slots}");
        Assert.True(engine.Query("fresh(X), X == 9.125.").Success);
    }

    [Fact]
    public void AnEnumerationInFlightReadsItsRetractedClausesWhileIdsAreReused()
    {
        var engine = Tier0(Churn);
        // item/1's chunks die under an enumeration of item/1 (a choice point
        // in its chain keeps the reclaim off), while other/1's churn frees
        // and reuses ids beside them: the logical update view, by content.
        const string goal = """
            assertz(item(1.5)), assertz(item(2.5)), assertz(item(3.5)),
            ( item(X), assertz(seen(X)), retract(item(X)), churn(1500), fail ; true ),
            findall(S, seen(S), L), L == [1.5, 2.5, 3.5], \+ item(_).
            """;
        Assert.True(engine.Query(goal).Success);
    }

    [Fact]
    public void ConsultingTheSameTextTwiceInternsNothingNewAndPinsIt()
    {
        const string program = """
            :- public p/3.
            p("hello", 2.5, f(a, b, c, d, e, f, g, h, i)).
            """;
        var engine = Tier0(program);
        var pools = engine._literalPools;
        Assert.True(engine.Query("""p(S, F, T), S == "hello".""").Success);
        int strings = pools.Strings.Count, floats = pools.Floats.Count, images = pools.Images.Count;
        int id = IdOf(pools.Strings, "hello");
        Assert.True(id >= 0);
        Assert.True(pools.Strings.IsPinned(id), "a consulted string is not pinned");
        engine.ConsultString(program);
        Assert.True(engine.Query("""p(S, F, T), S == "hello".""").Success);
        Assert.Equal(strings, pools.Strings.Count);
        Assert.Equal(floats, pools.Floats.Count);
        Assert.Equal(images, pools.Images.Count);
        Assert.Equal(0, pools.Sweep());   // nothing of the program's goes
        Assert.True(pools.Strings.IsPinned(id));
    }

    [Fact]
    public void ADynamicPredicatesSnapshotHoldsItsImagesAndReleasesThemWithTheCode()
    {
        const string program = """
            :- dynamic big/1.
            big(f(a, b, c, d, e, f, g, h, i)).
            big(g(1, 2, 3, 4, 5, 6, 7, 8, 9)).
            """;
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.IlPromotion.BackgroundCompilation = false;
        engine.ConsultString(program);
        var images = engine._literalPools.Images;
        for (int i = 0; i < 3; i++)
            Assert.True(engine.Query("findall(X, big(X), L), length(L, 2).").Success);
        Assert.True(Promoted(engine, "big/1"), engine.IlPromotion.DescribePromotionState());
        int count = images.Count;
        // Mutated: evicted; called: promoted again, the same images.
        Assert.True(engine.Query("assertz(big(h(0))).").Success);
        // The goals name the clauses' own terms: their images are the
        // clauses' (interned by content), so the pool does not grow.
        for (int i = 0; i < 3; i++)
            Assert.True(engine.Query("big(f(a, b, c, d, e, f, g, h, i)), big(g(1, 2, 3, 4, 5, 6, 7, 8, 9)), big(h(0)), findall(X, big(X), L), length(L, 3).").Success);
        Assert.True(Promoted(engine, "big/1"), engine.IlPromotion.DescribePromotionState());
        Assert.Equal(count, images.Count);
        // Abolished, the code goes with the next query's setup, and nothing
        // names the images: the sweep frees them.
        Assert.True(engine.Query("abolish(big/1).").Success);
        Assert.True(engine.Query("true.").Success);
        int freed = images.Sweep();
        Assert.True(freed >= 2, $"the snapshot's images were not released ({freed} freed)");
    }

    private static bool Promoted(PrologEngine e, string indicator)
    {
        foreach (int fid in e.IlPromotion.PromotedFunctorIds())
        {
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string name = AtomTable.GetById(atomId)?.Name ?? "";
            if ($"{name[(name.LastIndexOf('$') + 1)..]}/{arity}" == indicator) return true;
        }
        return false;
    }
}
