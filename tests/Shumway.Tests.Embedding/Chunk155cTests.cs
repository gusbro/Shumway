using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 155c: in-place new-bucket-key assertz for extensible-
/// indexed dynamic predicates. When an assertz introduces a key
/// that has no existing bucket, the runtime builds a fresh bucket
/// chain at the end of the buffer (containing every var-arg-at-0
/// clause's body followed by the new clause's body), then extends
/// the corresponding sub-switch table with the new
/// <c>(key → chain-head)</c> entry. No persistent-buffer rebuild
/// — only the case where the sub-switch of the new key's type
/// doesn't yet exist (e.g. first int assertz to an atom-only
/// predicate) falls back.
/// </summary>
public class Chunk155cTests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);
    private static int Fid(string n, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, arity);

    [Fact]
    public void Assertz_NewAtomKey_InPlaceNoRebuild()
    {
        // Hot indexed predicate. Assertz a clause whose arg-0 atom
        // is brand new — the switch_on_atom table didn't have it.
        // Chunk 155c adds the entry in place, no persistent rebuild.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        // Heat — these queries push the predicate over the JIT
        // threshold so the next setup re-links with the chunk-155a
        // indexed layout. From here on, the persistent buffer holds
        // the indexed dispatch.
        e.Query("d(a).");
        e.Query("d(b).");
        // Now assertz a brand-new key.
        e.Query("assertz(d(c)).");
        // The new key is dispatched through the sub-switch table —
        // we should find d(c) and only d(c) for the bucket query.
        Assert.True(e.Query("d(c).").Success);
        Assert.Single(e.QueryAll("d(c).").ToList());
        // And the existing keys are unaffected.
        Assert.True(e.Query("d(a).").Success);
        Assert.True(e.Query("d(b).").Success);
        // Var query enumerates all 3 in source order.
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new Term[] { Atom("a"), Atom("b"), Atom("c") }, xs);
    }

    [Fact]
    public void Assertz_NewIntKey_InPlaceNoRebuild()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic n/1.");
        e.Query("assertz(n(1)).");
        e.Query("assertz(n(2)).");
        e.Query("n(1).");
        e.Query("n(2).");
        // New int key.
        e.Query("assertz(n(99)).");
        Assert.True(e.Query("n(99).").Success);
        var xs = e.QueryAll("n(X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        Assert.Equal(new long[] { 1, 2, 99 }, xs);
    }

    [Fact]
    public void Assertz_NewKey_PreservesVarArgClauses()
    {
        // The fresh bucket chain must include every var-arg-at-0
        // clause so a query for the new key matches them too.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic p/2.\np(X, generic) :- atom(X).");
        // Note: the clause p(X, generic) has var arg-0.
        e.Query("assertz(p(a, 1)).");
        e.Query("assertz(p(b, 2)).");
        e.Query("p(a, _).");
        e.Query("p(b, _).");
        // Now assertz with a brand-new key.
        e.Query("assertz(p(c, 3)).");
        // Quick existence checks first.
        Assert.True(e.Query("p(c, 3).").Success);
        Assert.True(e.Query("p(c, generic).").Success);
        // Querying p(c, X) should yield (c, 3) AND (c, generic).
        // The (c, generic) match comes from the var-arg clause merged
        // into the new bucket.
        var cSols = e.QueryAll("p(c, X).").Select(s => s["X"]).ToList();
        Assert.Contains(Int(3), cSols);
        Assert.Contains(Atom("generic"), cSols);
    }

    [Fact]
    public void Assertz_NewStructKey_InPlace()
    {
        // Structure key buckets work the same way.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic p/1.");
        e.Query("assertz(p(foo(1))).");
        e.Query("assertz(p(bar(2))).");
        e.Query("p(foo(_)).");
        e.Query("p(bar(_)).");
        // New struct key (different functor).
        e.Query("assertz(p(baz(3))).");
        Assert.True(e.Query("p(baz(3)).").Success);
        var all = e.QueryAll("p(X).").Count();
        Assert.Equal(3, all);
    }

    [Fact]
    public void Assertz_FirstIntegerToAtomOnlyPredicate_FallsBack()
    {
        // Predicate has only atom clauses, so no switch_on_integer
        // sub-switch exists. First integer assertz can't add a new
        // bucket without inserting a new sub-switch (a layout
        // change) — chunk 155c falls back to rebuild here.
        // Correctness must hold via the rebuild.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic m/1.");
        e.Query("assertz(m(a)).");
        e.Query("assertz(m(b)).");
        e.Query("m(a).");
        e.Query("m(b).");
        // First int — sub-switch doesn't exist yet.
        e.Query("assertz(m(42)).");
        Assert.True(e.Query("m(42).").Success);
        Assert.True(e.Query("m(a).").Success);
        Assert.True(e.Query("m(b).").Success);
    }

    [Fact]
    public void Assertz_ManyNewKeys_AllInPlace()
    {
        // Stress: many new-key assertzes after promotion. Each one
        // adds to the switch table in place; the bucket chains and
        // bodies accumulate at the end of the buffer.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        e.Query("d(a).");
        e.Query("d(b).");
        // Add 20 new keys, each in place.
        for (int i = 0; i < 20; i++)
            e.Query($"assertz(d(k{i})).");
        // All visible.
        for (int i = 0; i < 20; i++)
            Assert.True(e.Query($"d(k{i}).").Success, $"d(k{i}) missing");
        Assert.True(e.Query("d(a).").Success);
        Assert.True(e.Query("d(b).").Success);
        // Total clauses.
        Assert.Equal(22, e.QueryAll("d(_).").Count());
    }
}
