using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 82 — compiled-static-predicate cache. Every query used to
/// recompile the whole program (every static predicate, plus the
/// prelude) from AST to WAM bytecode. Static predicates are immutable
/// between consults, so chunk 82 caches their compiled form on the
/// engine and reuses it — the per-query cost drops from O(program) to
/// O(goal). The cache is dropped wholesale on the next <c>consult</c>,
/// the only operation that changes the static program.
///
/// <para>These tests pin the cache's observable contract: it populates,
/// it is invalidated by a consult, and — most importantly — reusing
/// cached predicates across many queries never changes a result. The
/// <c>__query__</c> clause and any query-derived auxiliary predicates
/// are deliberately excluded, so one query's goal can't leak into the
/// next.</para>
/// </summary>
public class Chunk82Tests
{
    [Fact]
    public void Cache_IsEmptyBeforeAnyQuery_PopulatedAfter()
    {
        var engine = new PrologEngine();
        Assert.Empty(engine.StaticPredicateCache);
        engine.Query("true.");
        // The prelude's static predicates are now compiled and cached.
        Assert.NotEmpty(engine.StaticPredicateCache);
    }

    [Fact]
    public void Consult_KeepsUnchangedEntries_AndPicksUpTheNewClause()
    {
        // Invalidation is TARGETED, not wholesale: a consult leaves compiled
        // predicates of unchanged modules (the prelude's) in the cache — the
        // per-module transform fingerprint drops exactly the changed modules'
        // entries at the next query's product build — and the newly consulted
        // predicate is live from that same build.
        var engine = new PrologEngine();
        engine.Query("true.");
        Assert.NotEmpty(engine.StaticPredicateCache);
        engine.ConsultString(":- public p/1.\np(1).");
        Assert.NotEmpty(engine.StaticPredicateCache);   // prelude entries survive
        Assert.True(engine.Query("p(1).").Success);      // new clause is live
    }

    [Fact]
    public void RepeatedQueries_OnTheSameEngine_StayCorrect()
    {
        // The cross-query correctness the cache must preserve: control
        // constructs notably broke when an earlier query's compiled form
        // was wrongly reused.
        var engine = new PrologEngine();
        Assert.True(engine.Query("true ; fail.").Success);
        Assert.True(engine.Query("fail ; true.").Success);
        Assert.False(engine.Query("fail ; fail.").Success);
        Assert.True(engine.Query("\\+ fail.").Success);
        Assert.False(engine.Query("\\+ true.").Success);
    }

    [Fact]
    public void ConsultedPredicate_IsVisibleAfterCacheInvalidation()
    {
        // A query warms the cache; a later consult must still take
        // effect (the cache is dropped, the new clause is picked up).
        var engine = new PrologEngine();
        engine.ConsultString(":- public greet/1.\ngreet(hello).");
        Assert.True(engine.Query("greet(hello).").Success);
        engine.ConsultString(":- public greet/1.\ngreet(bye).");
        Assert.True(engine.Query("greet(bye).").Success);
        Assert.True(engine.Query("greet(hello).").Success);
    }

    [Fact]
    public void Findall_StaysCorrect_WithTheCache()
    {
        // findall runs in a sub-engine, which receives a copy of the
        // parent's static cache — results must be unaffected.
        var engine = new PrologEngine();
        var sols = engine.QueryAll("findall(X, member(X, [1,2,3]), L), member(X, L).")
            .ToList();
        Assert.True(engine.Query("findall(X, member(X, [a,b]), [a,b]).").Success);
    }

    [Fact]
    public void RepeatedQueries_OfAConsultedPredicate_StayCorrect()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- public color/1.\ncolor(red).\ncolor(green).\ncolor(blue).");
        for (int i = 0; i < 5; i++)
            Assert.Equal(3, engine.QueryAll("color(C).").Count());
        Assert.True(engine.Query("color(green).").Success);
        Assert.False(engine.Query("color(purple).").Success);
    }
}
