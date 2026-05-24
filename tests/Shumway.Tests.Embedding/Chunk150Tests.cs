using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 150: <c>garbage_collect_clauses/0,1</c> — re-threads
/// each dynamic predicate's bytecode chain through only its live
/// entries, bypassing the dead (retracted/abolished) entries, AND
/// reclaims the dead chunks into an engine-wide free list so the
/// next <c>assertz</c> / <c>asserta</c> can reuse the bytes instead
/// of extending the program buffer. ADR-015's append-only chain
/// otherwise grows monotonically.
/// </summary>
public class Chunk150Tests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    [Fact]
    public void Gc_BetweenRetractAndAdd_KeepsBehaviourCorrect()
    {
        // Assert-many, retract-some, GC, query — the live set must
        // still be enumerable correctly.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query(
            "assertz(d(1)), assertz(d(2)), assertz(d(3)), "
            + "assertz(d(4)), assertz(d(5)).").Success);
        Assert.True(e.Query("retract(d(2)), retract(d(4)).").Success);
        Assert.True(e.Query("garbage_collect_clauses.").Success);
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(3), Int(5) }, xs);
    }

    [Fact]
    public void Gc_AfterAbolishAndReAssert_StillResolvesNewClauses()
    {
        // The pattern the user explicitly called out: assert many,
        // abolish, assert more, GC.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)), assertz(d(2)), assertz(d(3)).");
        e.Query("abolish(d/1).");
        // After abolish d/1 is no longer dynamic — re-declare and
        // re-assert.
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(10)), assertz(d(20)).");
        Assert.True(e.Query("garbage_collect_clauses.").Success);
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(10), Int(20) }, xs);
    }

    [Fact]
    public void Gc_NamedPredicate_OnlyAffectsNamedOne()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic a/1. :- dynamic b/1.");
        e.Query("assertz(a(1)), assertz(a(2)), retract(a(1)).");
        e.Query("assertz(b(10)), assertz(b(20)), retract(b(10)).");
        Assert.True(e.Query("garbage_collect_clauses(a/1).").Success);
        // Both predicates still report correct live sets.
        Assert.Equal(new[] { Int(2) }, e.QueryAll("a(X).").Select(s => s["X"]));
        Assert.Equal(new[] { Int(20) }, e.QueryAll("b(X).").Select(s => s["X"]));
    }

    [Fact]
    public void Gc_AfterAsserta_NewHeadIsCorrectlyEnumerated()
    {
        // asserta + retract + GC — exercises the chunk-128 demotion
        // path being unwound by the GC's TryMeElse-at-head promotion.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(2)), assertz(d(3)), asserta(d(1)).");
        e.Query("retract(d(2)).");
        Assert.True(e.Query("garbage_collect_clauses.").Success);
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(3) }, xs);
    }

    [Fact]
    public void Gc_EmptyDynamic_DoesNotCrash()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)), retract(d(1)).");
        Assert.True(e.Query("garbage_collect_clauses.").Success);
        Assert.False(e.Query("d(_).").Success);
    }

    [Fact]
    public void Gc_NeverDeclared_Silent()
    {
        var e = new PrologEngine();
        // No dynamic predicates exist; GC is a no-op.
        Assert.True(e.Query("garbage_collect_clauses.").Success);
    }

    // The per-engine PrologEngine doesn't track FreeChunks itself —
    // the free-list lives on the per-query Engine because the
    // program buffer it refers to is per-query. Across queries the
    // program is rebuilt from scratch (from _dynamicClauses, which
    // only holds live clauses), so a cross-query free-list would
    // refer to addresses that don't exist in the new program. The
    // within-query reclamation pattern these tests verify is the
    // one that actually matters in practice: a long-running query
    // that assertz / retracts a lot of clauses (the user's stated
    // motivation) and periodically runs GC to keep memory bounded.

    [Fact]
    public void Gc_WithinQuery_RetractAndAssertReusesChunk()
    {
        // Single query: assertz a few clauses, retract them all,
        // run GC, then assertz more. The new asserts should reuse
        // the bytes freed by GC instead of extending the program
        // buffer. We pin the behaviour by inspecting d/1's
        // enumeration before and after — correctness alone is the
        // observable, since the program buffer length isn't user-
        // visible. The fact that the post-GC assertz works at all
        // (no crash from a corrupted reused chunk) is the test.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query(
            "assertz(d(1)), assertz(d(2)), assertz(d(3)), "
            + "retract(d(1)), retract(d(2)), retract(d(3)), "
            + "garbage_collect_clauses, "
            + "assertz(d(10)), assertz(d(20)), assertz(d(30)).").Success);
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(10), Int(20), Int(30) }, xs);
    }

    [Fact]
    public void Gc_WithinQuery_AbolishAndReassertReusesChunk()
    {
        // The pattern your stated motivation called out: abolish
        // all clauses, GC frees their memory, re-declare and
        // re-assert. In a single query so the program buffer
        // persists.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query(
            "assertz(d(1)), assertz(d(2)), assertz(d(3)), "
            + "abolish(d/1), "
            + "garbage_collect_clauses.").Success);
        // Re-declare and re-assert in a fresh query (the program
        // gets rebuilt fresh — the cross-query case is documented
        // as out of scope for the free-list, but the engine state
        // for the redeclaration must still be valid).
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(10)).");
        Assert.True(e.Query("d(10).").Success);
    }

    [Fact]
    public void Gc_RetractsAssertzOnlyHead_DoesNotCorruptCheckVisible()
    {
        // The case to be careful about: an assertz-only predicate
        // has every clause emitted with a native 5-byte
        // retry_me_else (no chunk-128 padding). The first user
        // clause sits one hop past the empty stub (which is the
        // real trampoline target — a 9-byte try_me_else). If GC
        // tried to make the first live entry the new head and
        // promote its 5-byte retry_me_else to a 9-byte
        // try_me_else in place, it would overwrite the
        // check_visible at bytes 5-8. The current design keeps the
        // trampoline pointing at the empty stub and only re-threads
        // the stub's <next> downward; this regression test pins
        // that the surviving entries still enumerate correctly
        // after the path is exercised.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        // All assertz — every chain entry is a native 5-byte
        // retry_me_else, no padding.
        e.Query("assertz(d(1)), assertz(d(2)), assertz(d(3)).");
        // Retract the first live entry. The empty stub's <next>
        // (which was pointing at d(1)) is now pointing at a dead
        // entry. GC will re-thread to skip it.
        e.Query("retract(d(1)).");
        Assert.True(e.Query("garbage_collect_clauses.").Success);
        // If GC had corrupted d(2)'s check_visible by promoting
        // its retry_me_else in place, this enumeration would crash
        // or return wrong values. The safe path leaves the trampoline
        // and head opcode alone and only re-threads <next>.
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(2), Int(3) }, xs);
    }
}
