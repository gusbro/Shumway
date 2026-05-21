using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 92 (Phase 6): the remaining CLP(FD) arithmetic expression
/// functions — <c>min</c>, <c>max</c>, <c>abs</c> and truncating
/// integer division <c>//</c> — and the <c>sum/3</c> constraint.
/// </summary>
public class Chunk92Tests
{
    private static Term Int(long v) => new IntTerm(v);

    private static PrologEngine Fd()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        return engine;
    }

    // ---- min / max ----

    [Fact]
    public void Min_OfGroundValues_Evaluates()
    {
        Assert.Equal(Int(3), Fd().Query("X #= min(3, 7).")["X"]);
    }

    [Fact]
    public void Max_OfGroundValues_Evaluates()
    {
        Assert.Equal(Int(7), Fd().Query("X #= max(3, 7).")["X"]);
    }

    [Fact]
    public void Min_PropagatesOnceOperandsAreBound()
    {
        Assert.Equal(Int(2), Fd().Query("X #= min(A, B), A = 5, B = 2.")["X"]);
    }

    [Fact]
    public void Max_PropagatesOnceOperandsAreBound()
    {
        Assert.Equal(Int(5), Fd().Query("X #= max(A, B), A = 5, B = 2.")["X"]);
    }

    [Fact]
    public void Min_LiftsOperandLowerBounds()
    {
        // X = min(A,B) and X >= 5 means both A and B are at least 5.
        Assert.False(Fd().Query(
            "A in 1..10, B in 1..10, X #= min(A, B), X #>= 5, A = 4.").Success);
        Assert.True(Fd().Query(
            "A in 1..10, B in 1..10, X #= min(A, B), X #>= 5, A = 6.").Success);
    }

    [Fact]
    public void Max_DropsOperandUpperBounds()
    {
        Assert.False(Fd().Query(
            "A in 1..10, B in 1..10, X #= max(A, B), X #=< 5, A = 8.").Success);
    }

    // ---- abs ----

    [Fact]
    public void Abs_OfNegativeValue_IsPositive()
    {
        Assert.Equal(Int(7), Fd().Query("X #= abs(A), A = -7.")["X"]);
    }

    [Fact]
    public void Abs_OfPositiveValue_IsUnchanged()
    {
        Assert.Equal(Int(4), Fd().Query("X #= abs(A), A = 4.")["X"]);
    }

    [Fact]
    public void Abs_ConfinesOperandToTheSymmetricRange()
    {
        // X = abs(A), X =< 3  =>  A in -3..3.
        Assert.True(Fd().Query(
            "A in -100..100, X #= abs(A), X #=< 3, A = -2.").Success);
        Assert.False(Fd().Query(
            "A in -100..100, X #= abs(A), X #=< 3, A = 50.").Success);
    }

    [Fact]
    public void Abs_ResultIsNeverNegative()
    {
        Assert.False(Fd().Query("A in -5..5, X #= abs(A), X #< 0.").Success);
    }

    // ---- // (truncating integer division) ----

    [Fact]
    public void Idiv_OfGroundValues_TruncatesTowardZero()
    {
        Assert.Equal(Int(3), Fd().Query("X #= 7 // 2.")["X"]);
    }

    [Fact]
    public void Idiv_NegativeDividend_TruncatesTowardZero()
    {
        Assert.Equal(Int(-3), Fd().Query("X #= A // 2, A = -7.")["X"]);
    }

    [Fact]
    public void Idiv_PropagatesForward()
    {
        Assert.Equal(Int(4), Fd().Query("X #= A // 3, A = 12.")["X"]);
    }

    [Fact]
    public void Idiv_NarrowsTheDividend()
    {
        Assert.True(Fd().Query("X #= A // 2, A in 0..100, X = 4, A = 8.").Success);
        Assert.False(Fd().Query("X #= A // 2, A in 0..100, X = 4, A = 6.").Success);
    }

    [Fact]
    public void Idiv_ByZero_RaisesEvaluationError()
    {
        Assert.True(Fd().Query(
            "catch(X #= A // 0, error(evaluation_error(zero_divisor), _), " +
            "true).").Success);
    }

    // ---- sum/3 ----

    [Fact]
    public void Sum_OfGroundList_Equals()
    {
        Assert.Equal(Int(6), Fd().Query("sum([1, 2, 3], #=, T).")["T"]);
    }

    [Fact]
    public void Sum_OfEmptyList_IsZero()
    {
        Assert.Equal(Int(0), Fd().Query("sum([], #=, T).")["T"]);
    }

    [Fact]
    public void Sum_PropagatesToAVariable()
    {
        Assert.Equal(Int(7), Fd().Query(
            "X in 1..10, Y in 1..10, sum([X, Y], #=, 10), X = 3.")["Y"]);
    }

    [Fact]
    public void Sum_WithLessThanRelation()
    {
        Assert.True(Fd().Query(
            "X in 1..10, Y in 1..10, sum([X, Y], #<, 5), X = 2, Y = 2.").Success);
        Assert.False(Fd().Query(
            "X in 1..10, Y in 1..10, sum([X, Y], #<, 5), X = 3, Y = 3.").Success);
    }

    [Fact]
    public void Sum_WithGreaterEqualRelation_DrivesOperands()
    {
        var sol = Fd().Query(
            "X in 1..5, Y in 1..5, sum([X, Y], #>=, 10).");
        Assert.True(sol.Success);
        Assert.Equal(Int(5), sol["X"]);
        Assert.Equal(Int(5), sol["Y"]);
    }

    [Fact]
    public void Sum_WithLabeling_EnumeratesSolutions()
    {
        var sols = Fd().QueryAll(
            "X in 1..3, Y in 1..3, Z in 1..3, sum([X, Y, Z], #=, 6), " +
            "label([X, Y, Z]).").ToList();
        Assert.Equal(7, sols.Count);
    }

    [Fact]
    public void Sum_UnknownRelation_RaisesDomainError()
    {
        Assert.True(Fd().Query(
            "catch(sum([_], foo, _), error(domain_error(clpfd_relation, foo), _), " +
            "true).").Success);
    }
}
