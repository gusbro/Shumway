using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 90 (Phase 6): CLP(FD) multiplication and labeling. The
/// <c>*</c> expression posts a bounds-consistent product propagator;
/// <c>label/1</c>, <c>labeling/2</c> (with <c>leftmost</c>/<c>ff</c> and
/// <c>up</c>/<c>down</c>) and <c>indomain/1</c> enumerate domain values,
/// running propagation between assignments.
/// </summary>
public class Chunk90Tests
{
    private static Term Int(long v) => new IntTerm(v);

    private static PrologEngine Fd()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        return engine;
    }

    // ---- multiplication ----

    [Fact]
    public void Times_BothGround_Evaluates()
    {
        Assert.Equal(Int(42), Fd().Query("X #= 6 * 7.")["X"]);
    }

    [Fact]
    public void Times_ConstantTimesVar_PropagatesForward()
    {
        Assert.Equal(Int(12), Fd().Query("X #= 3 * Y, Y = 4.")["X"]);
    }

    [Fact]
    public void Times_VarTimesConstant_PropagatesForward()
    {
        Assert.Equal(Int(30), Fd().Query("X #= Y * 5, Y = 6.")["X"]);
    }

    [Fact]
    public void Times_PropagatesBackward()
    {
        Assert.Equal(Int(5), Fd().Query("X #= 2 * Y, X = 10.")["Y"]);
    }

    [Fact]
    public void Times_NonDivisibleBackward_Fails()
    {
        // X = 2*Y has no integer solution for an odd X.
        Assert.False(Fd().Query("X #= 2 * Y, X = 7.").Success);
    }

    [Fact]
    public void Times_NegativeConstant_Propagates()
    {
        Assert.Equal(Int(-6), Fd().Query("X #= Y * (-2), Y = 3.")["X"]);
    }

    [Fact]
    public void Times_TwoVariables_NarrowsProduct()
    {
        Assert.True(Fd().Query("X in 2..3, Y in 4..5, Z #= X * Y, Z = 8.").Success);
        Assert.False(Fd().Query("X in 2..3, Y in 4..5, Z #= X * Y, Z = 7.").Success);
    }

    [Fact]
    public void Times_BothFactorsBound_DeterminesProduct()
    {
        Assert.Equal(Int(12),
            Fd().Query("X in 1..10, Y in 1..10, Z #= X * Y, X = 3, Y = 4.")["Z"]);
    }

    // ---- labeling: enumeration ----

    [Fact]
    public void Label_EnumeratesDomainInOrder()
    {
        var xs = Fd().QueryAll("X in 1..3, label([X]).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(2), Int(3) }, xs);
    }

    [Fact]
    public void Indomain_EnumeratesDomainInOrder()
    {
        var xs = Fd().QueryAll("X in 5..7, indomain(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(5), Int(6), Int(7) }, xs);
    }

    [Fact]
    public void Labeling_Down_EnumeratesHighToLow()
    {
        var xs = Fd().QueryAll("X in 1..3, labeling([down], [X]).")
            .Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(3), Int(2), Int(1) }, xs);
    }

    [Fact]
    public void Label_TwoVariables_ProducesEveryCombination()
    {
        var sols = Fd().QueryAll("X in 1..2, Y in 1..2, label([X, Y]).")
            .Select(s => (s["X"]!, s["Y"]!)).ToList();
        Assert.Equal(new[]
        {
            (Int(1), Int(1)), (Int(1), Int(2)),
            (Int(2), Int(1)), (Int(2), Int(2)),
        }, sols);
    }

    // ---- labeling: propagation prunes the search ----

    [Fact]
    public void Label_RespectsPostedConstraints()
    {
        var sols = Fd().QueryAll("X in 1..3, Y in 1..3, X #< Y, label([X, Y]).")
            .Select(s => (s["X"]!, s["Y"]!)).ToList();
        Assert.Equal(new[]
        {
            (Int(1), Int(2)), (Int(1), Int(3)), (Int(2), Int(3)),
        }, sols);
    }

    [Fact]
    public void Label_DrivesPropagationToOtherVariables()
    {
        // Labeling only Y; X is fixed by the #= constraint each step.
        // Y = 3 would force X = 4, outside 1..3, so that branch dies.
        var sols = Fd().QueryAll("X in 1..3, Y in 1..3, X #= Y + 1, label([Y]).")
            .Select(s => (s["Y"]!, s["X"]!)).ToList();
        Assert.Equal(new[] { (Int(1), Int(2)), (Int(2), Int(3)) }, sols);
    }

    [Fact]
    public void Label_SolvesAnAdditiveSumPuzzle()
    {
        var sols = Fd().QueryAll("X in 1..5, Y in 1..5, X + Y #= 6, label([X, Y]).")
            .Select(s => (s["X"]!, s["Y"]!)).ToList();
        Assert.Equal(new[]
        {
            (Int(1), Int(5)), (Int(2), Int(4)), (Int(3), Int(3)),
            (Int(4), Int(2)), (Int(5), Int(1)),
        }, sols);
    }

    [Fact]
    public void Label_WithMultiplicationConstraint()
    {
        var xs = Fd().QueryAll("Y in 1..4, X #= 2 * Y, label([Y]).")
            .Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(2), Int(4), Int(6), Int(8) }, xs);
    }

    // ---- first-fail ----

    [Fact]
    public void Labeling_FirstFail_FindsEverySolution()
    {
        var sols = Fd().QueryAll("X in 1..3, Y in 1..3, X #< Y, labeling([ff], [X, Y]).")
            .Select(s => (s["X"]!, s["Y"]!)).ToList();
        Assert.Equal(3, sols.Count);
        Assert.Contains((Int(1), Int(2)), sols);
        Assert.Contains((Int(2), Int(3)), sols);
    }

    // ---- error cases ----

    [Fact]
    public void Labeling_UnknownOption_RaisesDomainError()
    {
        Assert.True(Fd().Query(
            "catch(labeling([bogus], [_]), " +
            "error(domain_error(labeling_option, bogus), _), true).").Success);
    }

    [Fact]
    public void Label_UnboundedVariable_RaisesInstantiationError()
    {
        Assert.True(Fd().Query(
            "catch(label([_]), error(instantiation_error, _), true).").Success);
    }
}
