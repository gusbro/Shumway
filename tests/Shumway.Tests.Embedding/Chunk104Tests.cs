using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 104 (Phase 7): tabling. A <c>:- table p/N</c> predicate is
/// memoised and evaluated by a global naive fixpoint, so left-recursive
/// and cyclic definitions — which loop under plain SLD resolution —
/// terminate.
/// </summary>
public class Chunk104Tests
{
    private static PrologEngine WithProgram(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(program);
        return engine;
    }

    // A transitive-closure graph with a cycle (c -> a). The recursive
    // path/2 clause is left-recursive; untabled it loops forever.
    private const string CyclicGraph = """
        :- table path/2.
        edge(a, b).  edge(b, c).  edge(c, a).  edge(c, d).
        path(X, Y) :- edge(X, Y).
        path(X, Y) :- path(X, Z), edge(Z, Y).
        """;

    [Fact]
    public void LeftRecursionOverACycle_Terminates()
    {
        // From a, every node is reachable (a-b-c-d, and c-a closes the
        // cycle) — four answers, and the call terminates.
        var engine = WithProgram(CyclicGraph);
        Assert.Equal(4, engine.QueryAll("path(a, X).").Count());
    }

    [Fact]
    public void CyclicReachability_IncludesTheStartNode()
    {
        // a reaches itself through the cycle a-b-c-a.
        Assert.True(WithProgram(CyclicGraph).Query("path(a, a).").Success);
    }

    [Fact]
    public void GroundQueries_AgainstATabledPredicate()
    {
        var engine = WithProgram(CyclicGraph);
        Assert.True(engine.Query("path(a, d).").Success);
        Assert.False(engine.Query("path(d, a).").Success);  // d reaches nothing
    }

    [Fact]
    public void AcyclicTransitiveClosure_FindsEveryReachableNode()
    {
        var engine = WithProgram("""
            :- table reach/2.
            link(a, b).  link(b, c).  link(c, d).
            reach(X, Y) :- link(X, Y).
            reach(X, Y) :- reach(X, Z), link(Z, Y).
            """);
        Assert.Equal(3, engine.QueryAll("reach(a, X).").Count());  // b, c, d
    }

    [Fact]
    public void Tabling_DeduplicatesAnswersReachedMoreThanOneWay()
    {
        // d is reachable both via b and via c, but appears once.
        var engine = WithProgram("""
            :- table path/2.
            edge(a, b).  edge(a, c).  edge(b, d).  edge(c, d).
            path(X, Y) :- edge(X, Y).
            path(X, Y) :- path(X, Z), edge(Z, Y).
            """);
        Assert.Equal(3, engine.QueryAll("path(a, X).").Count());  // b, c, d
    }

    [Fact]
    public void TabledFacts_Enumerate()
    {
        var engine = WithProgram(":- table c/1.\nc(red).\nc(green).\nc(blue).");
        Assert.Equal(3, engine.QueryAll("c(X).").Count());
        Assert.True(engine.Query("c(green).").Success);
        Assert.False(engine.Query("c(yellow).").Success);
    }

    [Fact]
    public void MutualRecursion_Terminates()
    {
        // a and b are mutually recursive over a bounded counter; the
        // global fixpoint settles both together.
        var engine = WithProgram("""
            :- table a/1.
            :- table b/1.
            a(0).
            a(N) :- b(M), N is M + 1, N =< 3.
            b(N) :- a(N).
            """);
        Assert.Equal(4, engine.QueryAll("a(N).").Count());   // 0, 1, 2, 3
    }
}
