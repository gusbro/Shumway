using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 150: <c>garbage_collect_clauses/0,1</c> — re-threads
/// each dynamic predicate's bytecode chain through only its live
/// entries, bypassing the dead (retracted/abolished) entries.
/// ADR-015's append-only chain otherwise grows monotonically and
/// dispatch walks every entry ever asserted.
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
}
