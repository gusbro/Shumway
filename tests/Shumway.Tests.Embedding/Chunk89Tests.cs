using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 89 (Phase 6): the CLP(FD) library core — opt-in via
/// <see cref="PrologEngine.UseClpfd"/>, interval-list finite domains,
/// <c>in</c>/<c>ins</c>, and the six arithmetic constraints over additive
/// expressions with bounds propagation.
/// </summary>
public class Chunk89Tests
{
    private static Term Int(long v) => new IntTerm(v);

    private static PrologEngine Fd()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        return engine;
    }

    [Fact]
    public void UseClpfd_LoadsWithoutError()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
    }

    // ---- in / ins ----

    [Fact]
    public void In_ThenBindInRange_Succeeds()
    {
        Assert.True(Fd().Query("X in 1..10, X = 5.").Success);
    }

    [Fact]
    public void In_ThenBindOutOfRange_Fails()
    {
        Assert.False(Fd().Query("X in 1..10, X = 50.").Success);
    }

    [Fact]
    public void In_SingletonDomain_BindsVariable()
    {
        Assert.Equal(Int(7), Fd().Query("X in 7..7.")["X"]);
    }

    [Fact]
    public void Ins_ConstrainsEveryElement()
    {
        Assert.True(Fd().Query("ins([X,Y,Z], 1..3), X = 1, Y = 2, Z = 3.").Success);
        Assert.False(Fd().Query("ins([X,Y], 1..3), X = 9.").Success);
    }

    // ---- inequalities ----

    [Fact]
    public void Lt_NarrowsDomain()
    {
        Assert.True(Fd().Query("X in 1..10, X #< 4, X = 3.").Success);
        Assert.False(Fd().Query("X in 1..10, X #< 4, X = 4.").Success);
    }

    [Fact]
    public void Ge_CollapsingToSingleton_BindsVariable()
    {
        Assert.Equal(Int(10), Fd().Query("X in 1..10, X #>= 10.")["X"]);
    }

    [Fact]
    public void Le_CollapsingToSingleton_BindsVariable()
    {
        Assert.Equal(Int(1), Fd().Query("X in 1..10, X #=< 1.")["X"]);
    }

    [Fact]
    public void Lt_BetweenTwoVariables_Propagates()
    {
        // X in 1..10, Y in 1..10, X #< Y, Y = 2  =>  X = 1.
        Assert.Equal(Int(1), Fd().Query("X in 1..10, Y in 1..10, X #< Y, Y = 2.")["X"]);
    }

    [Fact]
    public void InconsistentInequalities_Fail()
    {
        Assert.False(Fd().Query("X in 1..10, Y in 1..10, X #< Y, Y #< X.").Success);
    }

    // ---- equality ----

    [Fact]
    public void Eq_UnifiesConstrainedVariables()
    {
        Assert.Equal(Int(5), Fd().Query("X #= Y, X = 5.")["Y"]);
    }

    [Fact]
    public void Eq_IntersectsDomains()
    {
        Assert.True(Fd().Query("X in 1..10, Y in 5..20, X #= Y, X = 7.").Success);
        Assert.False(Fd().Query("X in 1..10, Y in 5..20, X #= Y, X = 2.").Success);
    }

    // ---- disequality ----

    [Fact]
    public void Neq_RemovesValueLeavingSingleton()
    {
        Assert.Equal(Int(3), Fd().Query("X in 1..3, X #\\= 1, X #\\= 2.")["X"]);
    }

    [Fact]
    public void Neq_BindToRemovedValue_Fails()
    {
        Assert.False(Fd().Query("X in 1..5, X #\\= 3, X = 3.").Success);
    }

    // ---- additive expressions ----

    [Fact]
    public void Plus_PropagatesForward()
    {
        Assert.Equal(Int(4), Fd().Query("X #= Y + 1, Y = 3.")["X"]);
    }

    [Fact]
    public void Plus_PropagatesBackward()
    {
        Assert.Equal(Int(9), Fd().Query("X #= Y + 1, X = 10.")["Y"]);
    }

    [Fact]
    public void Minus_Propagates()
    {
        Assert.Equal(Int(4), Fd().Query("X #= Y - 3, Y = 7.")["X"]);
    }

    [Fact]
    public void Sum_OfThreeVariables_Propagates()
    {
        Assert.Equal(Int(6), Fd().Query("Z #= X + Y, X = 2, Y = 4.")["Z"]);
    }

    [Fact]
    public void Plus_NarrowsDomainsAcrossTheConstraint()
    {
        // X,Y in 1..10, X #= Y + 5  =>  X >= 6, Y =< 5.
        Assert.True(Fd().Query("X in 1..10, Y in 1..10, X #= Y + 5, X = 6.").Success);
        Assert.False(Fd().Query("X in 1..10, Y in 1..10, X #= Y + 5, Y = 6.").Success);
    }

    [Fact]
    public void UnaryMinus_Propagates()
    {
        Assert.Equal(Int(-4), Fd().Query("X #= -Y, Y = 4.")["X"]);
    }
}
