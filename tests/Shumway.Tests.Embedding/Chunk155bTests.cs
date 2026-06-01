using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 155b: in-place extension of chunk-155a's extensible-
/// indexed dynamic predicates for the common <c>assertz</c> case
/// (the new clause's arg-0 key matches an existing bucket). The
/// runtime walks the bucket chain and the var-fallthrough chain to
/// find their tail-next operands, appends a new body chunk + chain
/// entries at the end of the buffer, and patches the prior tails.
/// No persistent-buffer rebuild — the chunk-154 invalidate-on-mutate
/// fallback only fires for the cases this MVP can't handle yet
/// (new bucket key, var-arg-at-0 clause, asserta on indexed,
/// retract on indexed).
/// </summary>
public class Chunk155bTests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    [Fact]
    public void Assertz_SameKey_AfterPromotion_VisibleWithoutRebuild()
    {
        // Hot indexed predicate, assertz a clause whose key matches
        // an existing bucket. The next query must see the new clause
        // — and the dispatch path is in-place extension, not rebuild.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        // Heat up.
        e.Query("d(a).");
        e.Query("d(b).");
        // assertz of an existing-key clause.
        e.Query("assertz(d(a)).");
        // Bucket dispatch should see both d(a) clauses.
        var aSols = e.QueryAll("d(a).").Count();
        Assert.Equal(2, aSols);
        // Var dispatch sees all 3 clauses.
        var allSols = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new Term[] { Atom("a"), Atom("b"), Atom("a") }, allSols);
    }

    [Fact]
    public void Assertz_SameKey_MultiplePer_ExtensibleChain()
    {
        // Several assertz to the same key — each extends the bucket
        // chain by one entry.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(k)).");
        e.Query("assertz(d(other)).");  // makes 2 buckets
        e.Query("d(k).");
        e.Query("d(other).");
        // Now assertz several more to the 'k' bucket.
        e.Query("assertz(d(k)).");
        e.Query("assertz(d(k)).");
        e.Query("assertz(d(k)).");
        Assert.Equal(4, e.QueryAll("d(k).").Count());
        Assert.Single(e.QueryAll("d(other).").ToList());
    }

    [Fact]
    public void Assertz_IntegerKey_SameKeyExtension()
    {
        // Integer-keyed bucket — same code path, different switch
        // sub-dispatch (switch_on_integer).
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic n/1.");
        e.Query("assertz(n(1)).");
        e.Query("assertz(n(2)).");
        e.Query("n(1).");
        e.Query("n(2).");
        e.Query("assertz(n(1)).");
        Assert.Equal(2, e.QueryAll("n(1).").Count());
        Assert.Single(e.QueryAll("n(2).").ToList());
        var all = e.QueryAll("n(X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        Assert.Equal(new long[] { 1, 2, 1 }, all);
    }

    [Fact]
    public void Assertz_NewKey_FallsBackToRebuild_AndStillCorrect()
    {
        // A brand-new key triggers the rebuild fallback. Correctness
        // must hold — chunk 155b just routes through chunk 154's
        // path in this case.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        e.Query("d(a).");
        e.Query("d(b).");
        // Brand-new key.
        e.Query("assertz(d(c)).");
        Assert.True(e.Query("d(c).").Success);
        Assert.True(e.Query("d(a).").Success);
        Assert.True(e.Query("d(b).").Success);
    }

    [Fact]
    public void Assertz_WithinQuery_NewClauseVisible_ForSameKey()
    {
        // The ISO logical-update view: assertz then call within the
        // same query must see the asserted clause. With chunk-155b's
        // in-place extension, the live dispatch DOES contain the new
        // chain entry by the time the next call enters via
        // enter_dynamic.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(k)).");
        // Heat.
        e.Query("d(k).");
        e.Query("d(k).");
        // Within one query: assertz then call.
        Assert.True(e.Query("assertz(d(k)), d(k), d(k).").Success);
    }

    [Fact]
    public void Retract_OnHotIndexed_StillCorrect_ViaRebuild()
    {
        // Retract on indexed: chunk-155b falls back to rebuild via
        // the invalidate gate kept in RemoveDynamicByReference. The
        // surviving clauses must still enumerate.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)).");
        e.Query("assertz(d(2)).");
        e.Query("assertz(d(3)).");
        e.Query("d(1).");
        e.Query("d(2).");
        e.Query("retract(d(2)).");
        var xs = e.QueryAll("d(X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        Assert.Equal(new long[] { 1, 3 }, xs);
    }

    [Fact]
    public void ColdDynamic_StillUsesIncrementalChainExtension()
    {
        // Cold dynamic predicates haven't crossed the JIT threshold;
        // chunk-127's incremental chain-extension path applies.
        // Mid-query assertz extends in place — pinned to make sure
        // chunk-155b's invalidation reorganization doesn't regress
        // this.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query(
            "assertz(d(1)), d(1), assertz(d(2)), d(2), assertz(d(3)), d(3).").Success);
    }
}
