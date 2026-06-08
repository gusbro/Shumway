using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 350 (Phase 28): extends the chunk-349 self-tail-recursion in-method
/// loop from the indexed-dispatch path to single-clause predicates
/// (leaf / meta-CP) and try-me-else / switched chains. A chain re-reads the
/// incoming cursor to pick its clause, so a self-call resets the cursor to 0
/// and branches to the chain top — a fresh self-call must restart from clause 0,
/// not re-enter the clause it was called from. Verified through a persisted IL
/// bundle.
/// </summary>
public class Chunk350Tests
{
    private static PrologEngine LoadIl(string src)
    {
        var bundle = new Bundle(new[] { new BundleEntry("c350", src) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));
        return engine;
    }

    [Fact]
    public void SingleClauseSelfRecursion_DeepConstantStack()
    {
        // A single-clause tail-recursive predicate (no base clause; it fails
        // when the guard fails). The leaf in-method loop must run it 500k deep
        // in constant C# stack and fail cleanly (not overflow, not loop).
        var e = LoadIl(
            ":- public countdown/1.\n" +
            "countdown(N) :- N > 0, N1 is N - 1, countdown(N1).\n");
        Assert.False(e.Query("countdown(500000).").Success);
    }

    [Fact]
    public void ChainSelfRecursion_EnumeratesInClauseOrder()
    {
        // path/2: first arg is a var in both clauses, so no first-arg indexing
        // — a try-me-else / switched chain. The recursive path/2 in clause 2 is
        // a self tail call. If the cursor were NOT reset, the recursive call
        // would re-enter clause 2 (skip the direct edge in clause 1) and miss
        // solutions; resetting to 0 restarts at clause 1.
        var e = LoadIl(
            ":- public edge/2.\n:- public path/2.\n" +
            "edge(a, b).\n edge(b, c).\n" +
            "path(X, Y) :- edge(X, Y).\n" +
            "path(X, Y) :- edge(X, Z), path(Z, Y).\n");
        var ys = e.QueryAll("path(a, Y).")
            .Select(s => ((AtomTerm)s["Y"]).Name).ToList();
        // Direct edge a->b first (clause 1), then a->b->c via the recursion.
        Assert.Equal(new[] { "b", "c" }, ys);
    }

    [Fact]
    public void ChainSelfRecursion_FindsAllPathsWithBacktracking()
    {
        // A small DAG with two routes to d (via c and via e): the recursive
        // self call leaves choice points, and backtracking must restore each
        // one's own saved arguments across the in-method loop.
        var e = LoadIl(
            ":- public edge/2.\n:- public path/2.\n" +
            "edge(a, b).\n edge(b, c).\n edge(c, d).\n edge(b, e).\n edge(e, d).\n" +
            "path(X, Y) :- edge(X, Y).\n" +
            "path(X, Y) :- edge(X, Z), path(Z, Y).\n");
        var reached = e.QueryAll("path(a, Y).")
            .Select(s => ((AtomTerm)s["Y"]).Name).OrderBy(n => n).ToList();
        // a reaches b, c, e, and d (twice: a-b-c-d and a-b-e-d).
        Assert.Equal(new[] { "b", "c", "d", "d", "e" }, reached);
        Assert.True(e.Query("path(a, d).").Success);
        Assert.False(e.Query("path(a, a).").Success);
    }
}
