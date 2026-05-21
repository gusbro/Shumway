using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 100 (Phase 7): CLP(R) inequalities — <c>&lt;</c>, <c>&gt;</c>,
/// <c>=&lt;</c>, <c>&gt;=</c>. Each post gathers the connected component
/// of inequalities and tests it for satisfiability by Fourier–Motzkin
/// elimination, so an unsatisfiable system fails immediately.
/// </summary>
public class Chunk100Tests
{
    private static bool Holds(string query)
    {
        var engine = new PrologEngine();
        engine.UseClpr();
        return engine.Query(query).Success;
    }

    // ---- single-variable bounds ----

    [Fact]
    public void LowerBound_GatesTheValue()
    {
        Assert.True(Holds("{X >= 5, X =:= 5}."));
        Assert.False(Holds("{X >= 5, X =:= 4}."));
    }

    [Fact]
    public void UpperBound_GatesTheValue()
    {
        Assert.True(Holds("{X =< 10, X =:= 10}."));
        Assert.False(Holds("{X =< 10, X =:= 11}."));
    }

    [Fact]
    public void BoundInterval_AcceptsAValueInRange()
    {
        Assert.True(Holds("{X >= 1, X =< 10, X =:= 5}."));
    }

    [Fact]
    public void ContradictoryBounds_Fail()
    {
        Assert.False(Holds("{X >= 5, X =< 3}."));
    }

    // ---- strict vs non-strict ----

    [Fact]
    public void StrictInequality_ExcludesTheEndpoint()
    {
        Assert.False(Holds("{X > 0, X =:= 0}."));
        Assert.True(Holds("{X >= 0, X =:= 0}."));
    }

    [Fact]
    public void StrictBounds_Contradict()
    {
        Assert.False(Holds("{X > 0, X < 0}."));
    }

    // ---- coefficients ----

    [Fact]
    public void Coefficients_InAnInequality()
    {
        Assert.True(Holds("{2*X =< 10, X =:= 5}."));
        Assert.False(Holds("{2*X =< 10, X =:= 6}."));
    }

    // ---- multi-variable inequalities ----

    [Fact]
    public void MultiVariableInequality_Satisfiable()
    {
        Assert.True(Holds("{X + Y >= 10, X + Y =< 20}."));
    }

    [Fact]
    public void MultiVariableInequalities_Contradict()
    {
        // Neither inequality ever grounds; Fourier-Motzkin still detects
        // that their conjunction is unsatisfiable.
        Assert.False(Holds("{X + Y >= 10, X + Y =< 5}."));
    }

    [Fact]
    public void Inequality_DrivenInconsistentByEqualities()
    {
        Assert.True(Holds("{X + Y >= 10, X =:= 7, Y =:= 7}."));
        Assert.False(Holds("{X + Y >= 10, X =:= 3, Y =:= 3}."));
    }

    [Fact]
    public void Inequality_ReducedToABoundByAnEquality()
    {
        // Y = 5 turns X + Y =< 8 into X =< 3.
        Assert.True(Holds("{Y =:= 5, X + Y =< 8, X =:= 3}."));
        Assert.False(Holds("{Y =:= 5, X + Y =< 8, X =:= 4}."));
    }

    // ---- chains ----

    [Fact]
    public void ChainOfInequalities_Satisfiable()
    {
        Assert.True(Holds("{X >= 1, Y >= X, Z >= Y, Z =< 1}."));
    }

    [Fact]
    public void ChainOfInequalities_Contradict()
    {
        Assert.False(Holds("{X >= 1, Y >= X, Y =< 0}."));
    }

    [Fact]
    public void GreaterThan_AndLessThan()
    {
        Assert.True(Holds("{X > 2, X < 8, X =:= 5}."));
        Assert.False(Holds("{X > 2, X < 8, X =:= 8}."));
    }

    // ---- equalities still work after the rewrite ----

    [Fact]
    public void EqualitySystem_StillSolves()
    {
        Assert.True(Holds("{X + Y =:= 10, X - Y =:= 2, X =:= 6, Y =:= 4}."));
    }

    // ---- unsupported ----

    [Fact]
    public void Disequality_NotYetSupported()
    {
        Assert.True(Holds(
            "catch({X =\\= Y}, error(type_error(clpr_constraint, _), _), true)."));
    }
}
