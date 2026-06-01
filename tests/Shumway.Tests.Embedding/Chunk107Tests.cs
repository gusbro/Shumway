using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 107 (Phase 7): table invalidation and tabled negation.
/// <c>abolish_all_tables/0</c> and <c>abolish_table/1</c> discard cached
/// answers; <c>\+ Goal</c> over a tabled predicate is sound for
/// stratified programs (the negated subgoal is evaluated to completion
/// before it is tested).
/// </summary>
public class Chunk107Tests
{
    private static PrologEngine WithProgram(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(program);
        return engine;
    }

    [Fact]
    public void TabledNegation_FiltersOutNegatedAnswers()
    {
        var engine = WithProgram("""
            :- table good/1.
            :- table bad/1.
            num(1).  num(2).  num(3).  num(4).  num(5).
            bad(2).  bad(4).
            good(X) :- num(X), \+ bad(X).
            """);
        Assert.Equal(3, engine.QueryAll("good(X).").Count());   // 1, 3, 5
        Assert.True(engine.Query("good(3).").Success);
        Assert.False(engine.Query("good(4).").Success);
    }

    [Fact]
    public void TabledNegation_OverARecursiveTabledPredicate()
    {
        // unreach/1 negates reach/2 (a tabled transitive closure) — a
        // stratified use of negation: reach does not depend on unreach.
        var engine = WithProgram("""
            :- table reach/2.
            :- table unreach/1.
            edge(a, b).  edge(b, c).
            node(a).  node(b).  node(c).  node(d).
            reach(X, Y) :- edge(X, Y).
            reach(X, Y) :- reach(X, Z), edge(Z, Y).
            unreach(X) :- node(X), \+ reach(a, X).
            """);
        // From a: b and c are reachable; a (no cycle) and d (isolated)
        // are not.
        Assert.Equal(2, engine.QueryAll("unreach(X).").Count());
        Assert.True(engine.Query("unreach(d).").Success);
        Assert.False(engine.Query("unreach(b).").Success);
    }

    [Fact]
    public void AbolishAllTables_PicksUpProgramChanges()
    {
        var engine = WithProgram("""
            :- dynamic edge/2.
            :- table reach/2.
            edge(a, b).
            reach(X, Y) :- edge(X, Y).
            reach(X, Y) :- reach(X, Z), edge(Z, Y).
            """);
        Assert.Single(engine.QueryAll("reach(a, X)."));   // b

        engine.Query("assertz(edge(b, c)).");
        // The table still holds the pre-change answer set.
        Assert.Single(engine.QueryAll("reach(a, X)."));

        engine.Query("abolish_all_tables.");
        // Recomputed against the updated program.
        Assert.Equal(2, engine.QueryAll("reach(a, X).").Count());   // b, c
    }

    [Fact]
    public void AbolishTable_RecomputesTheNamedPredicate()
    {
        var engine = WithProgram("""
            :- dynamic edge/2.
            :- table reach/2.
            edge(a, b).
            reach(X, Y) :- edge(X, Y).
            reach(X, Y) :- reach(X, Z), edge(Z, Y).
            """);
        Assert.Single(engine.QueryAll("reach(a, X)."));
        engine.Query("assertz(edge(b, c)).");
        engine.Query("abolish_table(reach/2).");
        Assert.Equal(2, engine.QueryAll("reach(a, X).").Count());
    }
}
