using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 101 (Phase 7): CLP(R) disequality (<c>=\=</c>) and non-linear
/// constraints. A disequality fails only when the inequalities pin its
/// linear form to zero; a non-linear constraint is delayed and retried
/// when a variable it mentions is determined.
/// </summary>
public class Chunk101Tests
{
    private static bool Holds(string query)
    {
        var engine = new PrologEngine();
        engine.UseClpr();
        return engine.Query(query).Success;
    }

    // ---- disequality ----

    [Fact]
    public void Disequality_OfConstants()
    {
        Assert.True(Holds("{1 =\\= 2}."));
        Assert.False(Holds("{1 =\\= 1}."));
    }

    [Fact]
    public void Disequality_OfFreeVariables_IsSatisfiable()
    {
        Assert.True(Holds("{X =\\= Y}."));
    }

    [Fact]
    public void Disequality_ContradictedByAnEquality()
    {
        Assert.False(Holds("{X =\\= Y, X =:= Y}."));
    }

    [Fact]
    public void SelfDisequality_Fails()
    {
        Assert.False(Holds("{X =\\= X}."));
    }

    [Fact]
    public void Disequality_GroundToDistinctValues_Succeeds()
    {
        Assert.True(Holds("{X =\\= Y, X =:= 5, Y =:= 6}."));
        Assert.False(Holds("{X =\\= Y, X =:= 5, Y =:= 5}."));
    }

    [Fact]
    public void Disequality_PinnedToZeroByInequalities_Fails()
    {
        // X >= Y and X =< Y force X = Y, so X =\= Y cannot hold.
        Assert.False(Holds("{X =\\= Y, X >= Y, X =< Y}."));
    }

    [Fact]
    public void Disequality_WithLooseInequalities_Survives()
    {
        // X >= Y leaves X > Y possible, so X =\= Y stays satisfiable.
        Assert.True(Holds("{X =\\= Y, X >= Y}."));
    }

    // ---- non-linear constraints ----

    [Fact]
    public void NonLinearProduct_DelayedThenResolved()
    {
        // X*Y =:= 12 is delayed; once X = 3 it becomes 3*Y =:= 12 → Y = 4.
        Assert.True(Holds("{X * Y =:= 12, X =:= 3, Y =:= 4}."));
        Assert.False(Holds("{X * Y =:= 12, X =:= 3, Y =:= 5}."));
    }

    [Fact]
    public void NonLinearProduct_BothFactorsGivenUpFront()
    {
        Assert.True(Holds("{X =:= 3, Y =:= 4, X * Y =:= 12}."));
        Assert.False(Holds("{X =:= 3, Y =:= 4, X * Y =:= 13}."));
    }

    [Fact]
    public void NonLinearSquare_Resolves()
    {
        Assert.True(Holds("{X * X =:= 9, X =:= 3}."));
        Assert.False(Holds("{X * X =:= 9, X =:= 2}."));
    }

    [Fact]
    public void NonLinearQuotient_Resolves()
    {
        // X / Y =:= 2 is delayed; once Y = 4 it becomes linear → X = 8.
        Assert.True(Holds("{X / Y =:= 2, Y =:= 4, X =:= 8}."));
        Assert.False(Holds("{X / Y =:= 2, Y =:= 4, X =:= 9}."));
    }

    [Fact]
    public void NonLinearProduct_InAnInequality()
    {
        // X*Y =< 10 is delayed; with X = 2 it becomes 2*Y =< 10 → Y =< 5.
        Assert.True(Holds("{X * Y =< 10, X =:= 2, Y =:= 5}."));
        Assert.False(Holds("{X * Y =< 10, X =:= 2, Y =:= 6}."));
    }

    [Fact]
    public void TwoNonLinearConstraints_Contradict()
    {
        Assert.False(Holds("{X * Y =:= 12, X * Y =:= 10, X =:= 3}."));
    }

    [Fact]
    public void NonLinearConstraint_LeftResidual_Succeeds()
    {
        // Never resolved (both factors free) — kept as a residual.
        Assert.True(Holds("{X * Y =:= 6}."));
    }

    [Fact]
    public void DivisionByZero_RaisesEvaluationError()
    {
        Assert.True(Holds(
            "catch({X =:= 1 / 0}, error(evaluation_error(zero_divisor), _), " +
            "true)."));
    }
}
