using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 151b: persistent dynamic code space — the dynamic region
/// of the linked program lives in a buffer owned by
/// <c>PrologEngine</c> across queries; <c>assertz</c> / <c>asserta</c>
/// extend it in place and the next query reuses it without re-linking.
/// The per-query <c>__query__</c> clause and helpers live in a
/// separate overlay buffer at a logical address well above the
/// persistent end so mid-query growth doesn't collide. Chunk 150's
/// dead-chunk free-list moves to <c>PrologEngine</c> too — chunks
/// freed by <c>garbage_collect_clauses</c> in one query are reusable
/// by the next, the cross-query reclamation the chunk-150 commit
/// explicitly deferred.
/// </summary>
public class Chunk151bTests
{
    private static IntTerm Int(long v) => new(v);

    [Fact]
    public void AssertzAcrossQueries_AllVisible_ViaPersistentChain()
    {
        // The motivating regression: three assertzes in three separate
        // queries must all be visible to a later query. Pre-151b the
        // dynamic region was re-linked per query; the chunk-150 commit
        // proved that the within-query case worked but a separate
        // query rebuilt from _dynamicClauses, hiding the bytecode-chain
        // state. With the persistent buffer the bytecode chain *is*
        // the live state and survives across queries.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query("assertz(d(1)).").Success);
        Assert.True(e.Query("assertz(d(2)).").Success);
        Assert.True(e.Query("assertz(d(3)).").Success);
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(2), Int(3) }, xs);
    }

    [Fact]
    public void Asserta_AcrossQueries_OrderingPreserved()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(2)).");
        e.Query("assertz(d(3)).");
        e.Query("asserta(d(1)).");
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(2), Int(3) }, xs);
    }

    [Fact]
    public void Retract_AcrossQueries_HidesClause()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)).");
        e.Query("assertz(d(2)).");
        e.Query("assertz(d(3)).");
        e.Query("retract(d(2)).");
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(3) }, xs);
    }

    [Fact]
    public void GcAcrossQueries_FreedChunksReusedByNextQueryAssertz()
    {
        // The cross-query free-list — the piece the chunk-150 commit
        // deferred. Within one query: assertz many, retract all, GC
        // (free chunks land in the engine's free-list). In a later
        // query: assertz again, the free-listed chunks are reused
        // instead of extending the persistent buffer. Correctness
        // alone is the observable — the persistent buffer's growth
        // pattern is internal.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)), assertz(d(2)), assertz(d(3)).");
        e.Query("retract(d(1)), retract(d(2)), retract(d(3)).");
        e.Query("garbage_collect_clauses.");
        // Now in subsequent queries, assertz a fresh round. The
        // free-listed chunks (cross-query persistent) should be
        // reused — the test is that everything still enumerates
        // correctly and no crash from a corrupted reused chunk.
        e.Query("assertz(d(10)).");
        e.Query("assertz(d(20)).");
        e.Query("assertz(d(30)).");
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(10), Int(20), Int(30) }, xs);
    }

    [Fact]
    public void RebuildAfterAbolish_InvalidatesPersistentAndFreeList()
    {
        // abolish/1 drops a functor from the dynamic registry —
        // chunk 151b invalidates the persistent buffer (the layout
        // changes) and clears the free-list (its addresses point at
        // the now-stale buffer). The next query rebuilds persistent
        // cleanly.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)), assertz(d(2)).");
        e.Query("abolish(d/1).");
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(10)).");
        Assert.True(e.Query("d(10).").Success);
        Assert.False(e.Query("d(1).").Success);
    }

    [Fact]
    public void ManyAssertzAcrossManyQueries_NoStaleBufferReference()
    {
        // AppendCode reallocates and doubles when capacity is
        // exhausted. PrologEngine holds its own reference to the
        // persistent buffer; without the post-mutation
        // SyncPersistentFromEngine call, that reference is stale
        // after the first realloc and the next query reads stale
        // bytecode. The test forces several capacity growths by
        // asserting enough clauses to push past the initial size.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        const int n = 200;
        for (int i = 0; i < n; i++)
            e.Query($"assertz(d({i})).");
        var xs = e.QueryAll("d(X).").Select(s => (IntTerm)s["X"]!).ToList();
        Assert.Equal(n, xs.Count);
        for (int i = 0; i < n; i++)
            Assert.Equal((long)i, xs[i].Value);
    }
}
