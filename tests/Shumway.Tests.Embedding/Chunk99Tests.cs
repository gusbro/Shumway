using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 99 (Phase 7): the CLP(R) linear-equality core — the
/// <c>{Constraint}</c> wrapper, a Gaussian-elimination solver over the
/// reals, and binding of determined variables.
/// </summary>
public class Chunk99Tests
{
    private static PrologEngine Clpr()
    {
        var engine = new PrologEngine();
        engine.UseClpr();
        return engine;
    }

    private static bool Holds(string query) => Clpr().Query(query).Success;

    [Fact]
    public void UseClpr_LoadsWithoutError()
    {
        var engine = new PrologEngine();
        engine.UseClpr();
    }

    [Fact]
    public void SingleEquality_BindsTheVariable()
    {
        // CLP(R) works over the reals, so a determined variable is bound
        // to a (float) value; X =:= 7 confirms it is bound and equals 7.
        Assert.True(Holds("{X =:= 3 + 4}, X =:= 7."));
    }

    [Fact]
    public void TwoEquations_SolveTheSystem()
    {
        Assert.True(Holds("{X + Y =:= 10, X - Y =:= 2, X =:= 6, Y =:= 4}."));
    }

    [Fact]
    public void InconsistentEqualities_Fail()
    {
        Assert.False(Holds("{X =:= 1, X =:= 2}."));
    }

    [Fact]
    public void RedundantEquality_Succeeds()
    {
        Assert.True(Holds("{X =:= 5, X =:= 5}."));
    }

    [Fact]
    public void LinearCombinationWithCoefficients()
    {
        // 2X + 3Y = 12 with X = Y gives X = Y = 2.4.
        Assert.True(Holds("{2*X + 3*Y =:= 12, X =:= Y, X =:= 2.4}."));
        Assert.False(Holds("{2*X + 3*Y =:= 12, X =:= Y, X =:= 3}."));
    }

    [Fact]
    public void SubstitutionAcrossEquations()
    {
        Assert.True(Holds("{Y =:= X + 1, X =:= 5, Y =:= 6}."));
    }

    [Fact]
    public void EqualsIsAcceptedAsEquality()
    {
        Assert.True(Holds("{X = 2 * 3}, X =:= 6."));
    }

    [Fact]
    public void DivisionByAConstant()
    {
        Assert.True(Holds("{X =:= 10 / 4, X =:= 2.5}."));
    }

    [Fact]
    public void UnaryMinus()
    {
        Assert.True(Holds("{X =:= -Y, Y =:= 3, X =:= -3}."));
    }

    [Fact]
    public void ThreeVariableSystem()
    {
        Assert.True(Holds(
            "{X + 2*Y + Z =:= 10, X - Y =:= 1, Z =:= 3, X =:= 3, Y =:= 2}."));
    }

    [Fact]
    public void OverDeterminedInconsistentSystem_Fails()
    {
        // X + Y = 5 and X - Y = 1 force X = 3; X = 4 then contradicts.
        Assert.False(Holds("{X + Y =:= 5, X - Y =:= 1, X =:= 4}."));
    }

    [Fact]
    public void UnifyingADependentVariablePostsTheConstraint()
    {
        // {X =:= Y} makes X depend on Y; binding X = 5 must propagate
        // through the verify_attributes hook so Y is then 5.
        Assert.True(Holds("{X =:= Y}, X = 5, {Y =:= 5}."));
        Assert.False(Holds("{X =:= Y}, X = 5, {Y =:= 6}."));
    }

    [Fact]
    public void NonLinearProduct_RaisesTypeError()
    {
        Assert.True(Holds(
            "catch({X * Y =:= 6}, error(type_error(clpr_linear, _), _), true)."));
    }

    [Fact]
    public void InequalityConstraint_NotYetSupported()
    {
        Assert.True(Holds(
            "catch({X < 3}, error(type_error(clpr_constraint, _), _), true)."));
    }
}
