using System.Diagnostics;
using System.Text;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 106 (Phase 7): semi-naive tabling. A tabled clause's single
/// tabled body literal is differentiated — each round re-derives only
/// what a producer's <em>delta</em> (its newly gained answers) makes
/// possible, instead of re-deriving every answer from scratch every
/// round. Clauses with two-plus tabled literals, or a tabled call nested
/// in a control construct, are re-run every round undifferentiated —
/// still correct, just not accelerated.
/// </summary>
public class Chunk106Tests
{
    private static PrologEngine WithProgram(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(program);
        return engine;
    }

    [Fact]
    public void DeepChain_SemiNaiveScales()
    {
        // A 500-node chain has fixpoint depth 500. Naive evaluation
        // re-derives every answer in every round (~O(n²) total); semi-
        // naive derives each answer once. The engine-backed '$tbl_seen'
        // set keeps the per-answer dedup O(1), so the closure stays cheap.
        var sb = new StringBuilder(":- table path/2.\n");
        const int n = 500;
        for (int i = 1; i < n; i++) sb.Append($"edge({i}, {i + 1}).\n");
        sb.Append("path(X, Y) :- edge(X, Y).\n");
        sb.Append("path(X, Y) :- path(X, Z), edge(Z, Y).\n");

        var sw = Stopwatch.StartNew();
        var engine = WithProgram(sb.ToString());
        int count = engine.QueryAll("path(1, X).").Count();
        sw.Stop();

        Assert.Equal(n - 1, count);
        // ~2s standalone; the bound exists to catch a regression to naive
        // evaluation (minutes at n=500), so it's deliberately loose — at 20s
        // it flaked under full-suite 16-way parallel CPU contention
        // (20.5-30s observed on a green run, 2026-07-11).
        Assert.True(sw.Elapsed.TotalSeconds < 40,
            $"deep-chain closure took {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public void DoublingClosure_TwoTabledLiterals()
    {
        // path(X,Y) :- path(X,Z), path(Z,Y) has TWO tabled body literals,
        // so it is a "complex" clause: re-run every round undifferentiated.
        // The answer must still be the full transitive closure.
        var engine = WithProgram("""
            :- table path/2.
            edge(a, b).  edge(b, c).  edge(c, d).
            path(X, Y) :- edge(X, Y).
            path(X, Y) :- path(X, Z), path(Z, Y).
            """);
        Assert.Equal(3, engine.QueryAll("path(a, X).").Count());  // b, c, d
        Assert.True(engine.Query("path(a, d).").Success);
    }

    [Fact]
    public void TabledCallInsideDisjunction_StillCorrect()
    {
        // The recursive call sits inside a (;)/2 — a control construct —
        // so the clause is complex and runs every round undifferentiated.
        var engine = WithProgram("""
            :- table r/1.
            seed(1).
            r(X) :- ( seed(X) ; r(Y), X is Y + 1, X =< 5 ).
            """);
        Assert.Equal(5, engine.QueryAll("r(X).").Count());   // 1..5
    }

    [Fact]
    public void MutualRecursion_StillTerminates()
    {
        var engine = WithProgram("""
            :- table a/1.
            :- table b/1.
            a(0).
            a(N) :- b(M), N is M + 1, N =< 4.
            b(N) :- a(N).
            """);
        Assert.Equal(5, engine.QueryAll("a(N).").Count());   // 0..4
    }

    [Fact]
    public void CyclicClosure_StillCorrect()
    {
        var engine = WithProgram("""
            :- table path/2.
            edge(a, b).  edge(b, c).  edge(c, a).
            path(X, Y) :- edge(X, Y).
            path(X, Y) :- path(X, Z), edge(Z, Y).
            """);
        Assert.Equal(3, engine.QueryAll("path(a, X).").Count());
        Assert.True(engine.Query("path(a, a).").Success);
    }
}
