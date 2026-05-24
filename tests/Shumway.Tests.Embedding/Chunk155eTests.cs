using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 155e: in-place var-arg-at-0 assertz for extensible-indexed
/// dynamic predicates. When the new clause's arg-0 is a variable,
/// it matches every existing bucket key (a var-arg clause merges
/// into every bucket per the chunk-155a layout). The runtime walks
/// every chain — var fallthrough + list bucket + every bucket
/// reachable through the atom / integer / structure sub-switches —
/// and extends each with a new entry referencing the same shared
/// body. No persistent-buffer rebuild.
/// </summary>
public class Chunk155eTests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    [Fact]
    public void Assertz_VarArg_AfterPromotion_VisibleInEveryBucket()
    {
        // Hot indexed predicate with atom buckets. Assertz a var-arg
        // clause — it must appear in queries for ANY key.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/2.");
        e.Query("assertz(d(a, 1)).");
        e.Query("assertz(d(b, 2)).");
        e.Query("d(a, _).");
        e.Query("d(b, _).");
        // Now a var-arg clause: matches every arg-0.
        e.Query("assertz(d(_, generic)).");
        // Queries for known keys should find both the original and
        // the var-arg result.
        var aSols = e.QueryAll("d(a, X).").Select(s => s["X"]).ToList();
        Assert.Contains(Int(1), aSols);
        Assert.Contains(Atom("generic"), aSols);
        var bSols = e.QueryAll("d(b, X).").Select(s => s["X"]).ToList();
        Assert.Contains(Int(2), bSols);
        Assert.Contains(Atom("generic"), bSols);
    }

    [Fact]
    public void Assertz_VarArgClause_AddedToEveryBucket()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic p/1.");
        e.Query("assertz(p(a)).");
        e.Query("assertz(p(b)).");
        e.Query("assertz(p(c)).");
        e.Query("p(a).");
        e.Query("p(b).");
        // assertz a clause with var arg-0.
        e.Query("assertz(p(_)).");
        // For any concrete key, the bucket should now have 2
        // entries: the specific clause + the var-arg one.
        Assert.Equal(2, e.QueryAll("p(a).").Count());
        Assert.Equal(2, e.QueryAll("p(b).").Count());
        Assert.Equal(2, e.QueryAll("p(c).").Count());
        // The var-arg clause also accepts an unbound query — total
        // 4 var-chain entries (3 originals + 1 var-arg).
        Assert.Equal(4, e.QueryAll("p(X).").Count());
    }

    [Fact]
    public void Assertz_VarArg_MixedWithIntegerBuckets()
    {
        // Multiple sub-switch types: the var-arg clause must extend
        // every bucket in every sub-switch table.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(1)).");
        e.Query("d(a).");
        e.Query("d(1).");
        e.Query("assertz(d(_)).");
        // Both kinds of keys see the var-arg.
        Assert.Equal(2, e.QueryAll("d(a).").Count());
        Assert.Equal(2, e.QueryAll("d(1).").Count());
    }

    [Fact]
    public void Assertz_MultipleVarArgs_StackedInEveryChain()
    {
        // Several var-arg assertzes stack in each bucket and the
        // var chain.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("d(a).");
        e.Query("d(a).");
        for (int i = 0; i < 3; i++) e.Query("assertz(d(_)).");
        // 'a' bucket: 1 original + 3 var-args = 4 entries.
        Assert.Equal(4, e.QueryAll("d(a).").Count());
        // Var query: 1 original + 3 var-args.
        Assert.Equal(4, e.QueryAll("d(X).").Count());
    }
}
