using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 54: plus/3 builtin + library predicates added to the prelude
/// (forall/2, maplist/2-4, foldl/4-5, aggregate_all/3).
/// </summary>
public class Chunk54Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ============================================================================
    // plus/3
    // ============================================================================

    [Fact]
    public void Plus_ComputesZ()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(7), engine.Query("plus(3, 4, Z).")["Z"]);
    }

    [Fact]
    public void Plus_ComputesY()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(4), engine.Query("plus(3, Y, 7).")["Y"]);
    }

    [Fact]
    public void Plus_ComputesX()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(3), engine.Query("plus(X, 4, 7).")["X"]);
    }

    [Fact]
    public void Plus_VerifiesEquation()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("plus(2, 5, 7).").Success);
        Assert.False(engine.Query("plus(2, 5, 8).").Success);
    }

    // ============================================================================
    // forall/2
    // ============================================================================

    [Fact]
    public void Forall_VacuouslyTrueOnEmpty()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("forall(fail, true).").Success);
    }

    [Fact]
    public void Forall_AllElementsSatisfyPredicate()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "forall(member(X, [1, 2, 3]), integer(X)).").Success);
    }

    [Fact]
    public void Forall_FailsOnCounterexample()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query(
            "forall(member(X, [1, foo, 3]), integer(X)).").Success);
    }

    // ============================================================================
    // maplist/2-4
    // ============================================================================

    [Fact]
    public void Maplist_Arity2_ChecksEveryElement()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("maplist(integer, [1, 2, 3]).").Success);
        Assert.False(engine.Query("maplist(integer, [1, foo, 3]).").Success);
    }

    [Fact]
    public void Maplist_Arity3_TransformsList()
    {
        var engine = new PrologEngine();
        // maplist(succ, [1, 2, 3], Out) → Out = [2, 3, 4].
        var sol = engine.Query("maplist(succ, [1, 2, 3], Out).");
        Assert.True(sol.Success);
        // Verify the result is [2, 3, 4].
        Assert.Equal(
            new CompoundTerm(".", new Term[] { Int(2),
                new CompoundTerm(".", new Term[] { Int(3),
                    new CompoundTerm(".", new Term[] { Int(4), Atom("[]") }) }) }),
            sol["Out"]);
    }

    // ============================================================================
    // foldl/4
    // ============================================================================

    [Fact]
    public void Foldl_SumsList()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- public sum_step/3.\nsum_step(X, Acc, Out) :- Out is Acc + X.");
        var sol = engine.Query("foldl(sum_step, [1, 2, 3, 4], 0, Total).");
        Assert.True(sol.Success);
        Assert.Equal(Int(10), sol["Total"]);
    }

    [Fact]
    public void Foldl_AcceptsAccumulatorChain()
    {
        // foldl with the accumulator passed through unchanged should yield
        // the initial accumulator at the end (the chain just observes).
        var engine = new PrologEngine();
        engine.ConsultString(":- public noop/3.\nnoop(_, A, A).");
        var sol = engine.Query("foldl(noop, [a, b, c], hello, Out).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hello"), sol["Out"]);
    }

    // ============================================================================
    // aggregate_all/3
    // ============================================================================

    [Fact]
    public void AggregateAll_Count()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(3),
            engine.Query("aggregate_all(count, member(_, [a, b, c]), N).")["N"]);
    }

    [Fact]
    public void AggregateAll_Sum()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(15),
            engine.Query("aggregate_all(sum(X), member(X, [1, 2, 3, 4, 5]), S).")["S"]);
    }

    [Fact]
    public void AggregateAll_Bag()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("aggregate_all(bag(X), member(X, [a, b, a]), B).");
        Assert.True(sol.Success);
        Assert.Equal(
            new CompoundTerm(".", new Term[] { Atom("a"),
                new CompoundTerm(".", new Term[] { Atom("b"),
                    new CompoundTerm(".", new Term[] { Atom("a"), Atom("[]") }) }) }),
            sol["B"]);
    }

    [Fact]
    public void AggregateAll_Set_DedupesAndSorts()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("aggregate_all(set(X), member(X, [c, a, b, a]), S).");
        Assert.True(sol.Success);
        Assert.Equal(
            new CompoundTerm(".", new Term[] { Atom("a"),
                new CompoundTerm(".", new Term[] { Atom("b"),
                    new CompoundTerm(".", new Term[] { Atom("c"), Atom("[]") }) }) }),
            sol["S"]);
    }
}
