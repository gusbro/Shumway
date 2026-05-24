using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 153 — first-argument indexing under chunk 151b's persistent
/// code space. Two regressions to pin:
///
/// <list type="bullet">
/// <item>A dynamic predicate that's been promoted to indexed dispatch
///   (chunk 75 JIT) must still see runtime mutations: an
///   <c>assertz</c> after promotion is visible to the next query, and
///   a <c>retract</c> hides the matching clause. Under chunk 151b
///   the indexed bytecode lives in the persistent buffer; the
///   incremental chain-patching path (chunk 127/128) only knows how
///   to extend a chain, not an indexed dispatch. The fix invalidates
///   the persistent buffer when an indexed dynamic predicate is
///   mutated, forcing the next query to re-link with fresh
///   indexing.</item>
/// <item>A cold dynamic predicate that becomes hot mid-life triggers
///   a JIT re-promotion; the persistent buffer must rebuild so the
///   running dispatch picks up the indexed form.</item>
/// </list>
/// </summary>
public class Chunk153Tests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    [Fact]
    public void IndexedDynamic_RetractAfterPromotion_HidesClause()
    {
        // Force JIT to promote on the very first call so the indexed
        // bytecode lands in persistent quickly.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic color/1.");
        e.Query("assertz(color(red)).");
        e.Query("assertz(color(green)).");
        e.Query("assertz(color(blue)).");
        // Two queries to cross threshold + recompile indexed.
        Assert.True(e.Query("color(red).").Success);
        Assert.True(e.Query("color(green).").Success);
        // Retract green — should be invisible to the next query.
        Assert.True(e.Query("retract(color(green)).").Success);
        Assert.True(e.Query("color(red).").Success);
        Assert.False(e.Query("color(green).").Success);
        Assert.True(e.Query("color(blue).").Success);
    }

    [Fact]
    public void IndexedDynamic_AssertzAfterPromotion_NewClauseVisible()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)).");
        e.Query("assertz(d(2)).");
        // Promote: two queries past the threshold-1 mark.
        e.Query("d(1).");
        e.Query("d(2).");
        // After promotion, assertz a new clause. It must be visible.
        e.Query("assertz(d(3)).");
        Assert.True(e.Query("d(3).").Success);
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(2), Int(3) }, xs);
    }

    [Fact]
    public void ColdToHot_TriggersPersistentRebuild()
    {
        // Cold first: dispatched through a chain. Hot after threshold
        // crossings: must dispatch indexed. The next query after the
        // flip must see the indexed bytecode in the running buffer,
        // not the stale chain.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 3;
        e.ConsultString(":- dynamic v/1.");
        e.Query("assertz(v(a)).");
        e.Query("assertz(v(b)).");
        e.Query("assertz(v(c)).");
        // Three cold queries.
        Assert.True(e.Query("v(a).").Success);
        Assert.True(e.Query("v(b).").Success);
        Assert.True(e.Query("v(c).").Success);
        // Now hot — next query should dispatch through indexed code,
        // and the right answers must still come out.
        var xs = e.QueryAll("v(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Atom("a"), Atom("b"), Atom("c") }, xs);
    }
}
