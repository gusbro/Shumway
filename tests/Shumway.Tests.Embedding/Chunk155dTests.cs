using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 155d: in-place retract for extensible-indexed dynamic
/// predicates. <c>RemoveDynamicByReference</c> walks every chain
/// (each bucket reached through the sub-switch tables + the var-
/// fallthrough chain + the list bucket) and patches the died slot
/// of every chain entry whose <c>execute</c> targets the retired
/// clause's body. No persistent-buffer rebuild — the in-flight
/// queries that captured a smaller view-gen still see the clause
/// (the ISO logical-update view).
/// </summary>
public class Chunk155dTests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    [Fact]
    public void RetractAfterPromotion_HidesClause_NoRebuild()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)).");
        e.Query("assertz(d(2)).");
        e.Query("assertz(d(3)).");
        // Heat.
        e.Query("d(1).");
        e.Query("d(2).");
        // Retract the middle clause.
        Assert.True(e.Query("retract(d(2)).").Success);
        // Bucket queries.
        Assert.True(e.Query("d(1).").Success);
        Assert.False(e.Query("d(2).").Success);
        Assert.True(e.Query("d(3).").Success);
        // Var query.
        var xs = e.QueryAll("d(X).").Select(s => ((IntTerm)s["X"]).Value).ToList();
        Assert.Equal(new long[] { 1, 3 }, xs);
    }

    [Fact]
    public void RetractAll_FromIndexedDynamic_LeavesEmpty()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        e.Query("d(a).");
        e.Query("d(b).");
        // Retract both.
        Assert.True(e.Query("retract(d(a)).").Success);
        Assert.True(e.Query("retract(d(b)).").Success);
        Assert.False(e.Query("d(_).").Success);
    }

    [Fact]
    public void Retract_PatchesBothBucketAndVarChain()
    {
        // The matched clause appears in (at least) the var
        // fallthrough chain and its specific bucket chain. The
        // retract must patch BOTH entries so both dispatch paths
        // skip the clause.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        e.Query("d(a).");
        e.Query("d(b).");
        e.Query("retract(d(a)).");
        // Bucket query (uses bucket chain).
        Assert.False(e.Query("d(a).").Success);
        // Var query (uses var fallthrough chain).
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new Term[] { Atom("b") }, xs);
    }

    [Fact]
    public void Retract_PreservesOtherKeysBucketChains()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/2.");
        e.Query("assertz(d(a, 1)).");
        e.Query("assertz(d(b, 2)).");
        e.Query("assertz(d(c, 3)).");
        e.Query("d(a, _).");
        e.Query("d(b, _).");
        e.Query("retract(d(b, 2)).");
        Assert.True(e.Query("d(a, 1).").Success);
        Assert.True(e.Query("d(c, 3).").Success);
        Assert.False(e.Query("d(b, _).").Success);
    }

    [Fact]
    public void RetractThenAssertz_SameKey_Works()
    {
        // Retract a clause, then assertz another for the same key.
        // The bucket chain should be intact and the new entry
        // appended.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(k)).");
        e.Query("assertz(d(other)).");
        e.Query("d(k).");
        e.Query("d(other).");
        e.Query("retract(d(k)).");
        e.Query("assertz(d(k)).");
        Assert.True(e.Query("d(k).").Success);
    }

    [Fact]
    public void RetractThenAssertz_NewKey_Works()
    {
        // After retract, assertz a brand-new key. The new-bucket-key
        // path (chunk 155c) should still work.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        e.Query("d(a).");
        e.Query("d(b).");
        e.Query("retract(d(a)).");
        e.Query("assertz(d(c)).");
        Assert.False(e.Query("d(a).").Success);
        Assert.True(e.Query("d(b).").Success);
        Assert.True(e.Query("d(c).").Success);
    }
}
