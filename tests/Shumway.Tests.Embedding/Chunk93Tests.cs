using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 93 (Phase 6): the CLP(FD) refinements that complete the
/// library — a stronger <c>all_distinct/1</c> with Hall-interval
/// pruning, <c>scalar_product/4</c>, and truncating division <c>//</c>
/// with a variable divisor.
/// </summary>
public class Chunk93Tests
{
    private static Term Int(long v) => new IntTerm(v);

    private static PrologEngine Fd()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        return engine;
    }

    // ---- all_distinct: Hall-interval pruning ----

    [Fact]
    public void AllDistinct_HallInterval_PrunesTheNonContainedVariable()
    {
        // X and Y fill {1,2}, so Z — the only one that could be 3 — must.
        var sol = Fd().Query(
            "X in 1..2, Y in 1..2, Z in 1..3, all_distinct([X, Y, Z]).");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["Z"]);
    }

    [Fact]
    public void AllDistinct_HallInterval_RemovesARangeFromOthers()
    {
        // X,Y,Z fill 1..3, so W loses 1..3 and is confined to 4..5.
        Assert.False(Fd().Query(
            "X in 1..3, Y in 1..3, Z in 1..3, W in 1..5, " +
            "all_distinct([X, Y, Z, W]), W = 2.").Success);
        Assert.True(Fd().Query(
            "X in 1..3, Y in 1..3, Z in 1..3, W in 1..5, " +
            "all_distinct([X, Y, Z, W]), W = 4.").Success);
    }

    [Fact]
    public void AllDistinct_Pigeonhole_FailsWithoutLabeling()
    {
        // Three variables, two values — Hall pruning fails immediately,
        // no search needed (unlike the weaker all_different/1).
        Assert.False(Fd().Query(
            "X in 1..2, Y in 1..2, Z in 1..2, all_distinct([X, Y, Z]).").Success);
    }

    [Fact]
    public void AllDistinct_PropagatesGroundValuesToOthers()
    {
        var sol = Fd().Query(
            "X in 1..3, Y in 1..3, Z in 1..3, all_distinct([X, Y, Z]), " +
            "X = 1, Y = 2.");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["Z"]);
    }

    [Fact]
    public void AllDistinct_WithLabeling_EnumeratesEveryPermutation()
    {
        var sols = Fd().QueryAll(
            "X in 1..3, Y in 1..3, Z in 1..3, all_distinct([X, Y, Z]), " +
            "label([X, Y, Z]).").ToList();
        Assert.Equal(6, sols.Count);
    }

    [Fact]
    public void AllDistinct_GroundLists_CheckDistinctness()
    {
        Assert.True(Fd().Query("all_distinct([3, 1, 2]).").Success);
        Assert.False(Fd().Query("all_distinct([1, 2, 1]).").Success);
    }

    // ---- scalar_product/4 ----

    [Fact]
    public void ScalarProduct_OfGroundValues_Evaluates()
    {
        // 2*4 + 3*5 = 23.
        Assert.Equal(Int(23),
            Fd().Query("scalar_product([2, 3], [4, 5], #=, T).")["T"]);
    }

    [Fact]
    public void ScalarProduct_PropagatesToAVariable()
    {
        Assert.Equal(Int(7),
            Fd().Query("scalar_product([1, 1], [X, Y], #=, 10), X = 3.")["Y"]);
    }

    [Fact]
    public void ScalarProduct_WithCoefficients_Propagates()
    {
        // 2*0 + 3*Y = 12  =>  Y = 4.
        Assert.Equal(Int(4),
            Fd().Query("scalar_product([2, 3], [X, Y], #=, 12), X = 0.")["Y"]);
    }

    [Fact]
    public void ScalarProduct_WithInequalityRelation()
    {
        Assert.True(Fd().Query(
            "scalar_product([2, 1], [X, Y], #=<, 7), X = 3, Y = 1.").Success);
        Assert.False(Fd().Query(
            "scalar_product([2, 1], [X, Y], #=<, 7), X = 3, Y = 2.").Success);
    }

    [Fact]
    public void ScalarProduct_WithLabeling_EnumeratesSolutions()
    {
        // 2X + 3Y = 6 over 0..3: (0,2) and (3,0).
        var sols = Fd().QueryAll(
            "X in 0..3, Y in 0..3, scalar_product([2, 3], [X, Y], #=, 6), " +
            "label([X, Y]).").Select(s => (s["X"]!, s["Y"]!)).ToList();
        Assert.Equal(new[] { (Int(0), Int(2)), (Int(3), Int(0)) }, sols);
    }

    [Fact]
    public void ScalarProduct_LengthMismatch_RaisesTypeError()
    {
        Assert.True(Fd().Query(
            "catch(scalar_product([1, 2], [_], #=, _), " +
            "error(type_error(clpfd_scalar_product_lengths, _), _), true).").Success);
    }

    [Fact]
    public void ScalarProduct_UnknownRelation_RaisesDomainError()
    {
        Assert.True(Fd().Query(
            "catch(scalar_product([1], [_], bogus, _), " +
            "error(domain_error(clpfd_relation, bogus), _), true).").Success);
    }

    // ---- // with a variable divisor ----

    [Fact]
    public void Idiv_VariableDivisor_BoundsTheQuotient()
    {
        // 10 // B for B in 2..5 lies in 2..5.
        Assert.True(Fd().Query("A = 10, B in 2..5, X #= A // B, X = 5.").Success);
        Assert.False(Fd().Query("A = 10, B in 2..5, X #= A // B, X = 1.").Success);
    }

    [Fact]
    public void Idiv_VariableDivisor_GroundsToExactQuotient()
    {
        Assert.Equal(Int(4), Fd().Query("A = 12, X #= A // B, B = 3.")["X"]);
    }

    [Fact]
    public void Idiv_VariableDivisor_WithLabeling()
    {
        var xs = Fd().QueryAll("A = 12, B in 1..4, X #= A // B, label([B]).")
            .Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(12), Int(6), Int(4), Int(3) }, xs);
    }
}
