using System.Diagnostics;
using System.Text;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 105 (Phase 7): tabling performance. Each subgoal's answers are
/// now a single sorted, duplicate-free list, so a fixpoint pass costs one
/// <c>sort/2</c> instead of a membership scan per solution — the pass
/// drops from O(n²) to O(n log n), which is what lets tabling scale past
/// toy inputs.
/// </summary>
public class Chunk105Tests
{
    private static PrologEngine WithProgram(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(program);
        return engine;
    }

    /// <summary>A linear chain 1->2->...->N as a tabled transitive
    /// closure. Reaches N-1 nodes from node 1.</summary>
    private static string Chain(int n)
    {
        var sb = new StringBuilder(":- table path/2.\n");
        for (int i = 1; i < n; i++) sb.Append($"edge({i}, {i + 1}).\n");
        sb.Append("path(X, Y) :- edge(X, Y).\n");
        sb.Append("path(X, Y) :- path(X, Z), edge(Z, Y).\n");
        return sb.ToString();
    }

    [Fact]
    public void LargeChain_TransitiveClosureScales()
    {
        var sw = Stopwatch.StartNew();
        var engine = WithProgram(Chain(150));
        int count = engine.QueryAll("path(1, X).").Count();
        sw.Stop();

        Assert.Equal(149, count);
        // A generous ceiling — the point is that it does not blow up.
        Assert.True(sw.Elapsed.TotalSeconds < 30,
            $"tabled 150-node closure took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void DenseGraph_RedundantPathsDeduplicated()
    {
        // Every node links to the next two, so each node is reachable by
        // exponentially many distinct paths — the answer set stays small
        // only because answers are deduplicated.
        var sb = new StringBuilder(":- table path/2.\n");
        const int n = 60;
        for (int i = 1; i <= n; i++)
        {
            if (i + 1 <= n) sb.Append($"edge({i}, {i + 1}).\n");
            if (i + 2 <= n) sb.Append($"edge({i}, {i + 2}).\n");
        }
        sb.Append("path(X, Y) :- edge(X, Y).\n");
        sb.Append("path(X, Y) :- path(X, Z), edge(Z, Y).\n");

        var engine = WithProgram(sb.ToString());
        Assert.Equal(n - 1, engine.QueryAll("path(1, X).").Count());
    }

    [Fact]
    public void LargeCycle_AllNodesReachable()
    {
        // A ring 1->2->...->N->1: from node 1 every node is reachable,
        // including node 1 itself.
        const int n = 100;
        var sb = new StringBuilder(":- table path/2.\n");
        for (int i = 1; i <= n; i++)
            sb.Append($"edge({i}, {(i % n) + 1}).\n");
        sb.Append("path(X, Y) :- edge(X, Y).\n");
        sb.Append("path(X, Y) :- path(X, Z), edge(Z, Y).\n");

        var engine = WithProgram(sb.ToString());
        Assert.Equal(n, engine.QueryAll("path(1, X).").Count());
        Assert.True(engine.Query("path(1, 1).").Success);
    }

    [Fact]
    public void SmallCases_StillCorrect()
    {
        // The sorted-list representation must not change answers.
        var engine = WithProgram("""
            :- table c/1.
            c(red).  c(green).  c(blue).
            """);
        Assert.Equal(3, engine.QueryAll("c(X).").Count());
    }
}
