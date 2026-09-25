using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A CLP(R) variable constrained only by inequalities must refuse a
/// value they exclude, whether the value arrives through the solver or
/// through plain unification.
///
/// <para>Two holes let it through. The hook handed back no goals for a
/// par(Cons) attribute -- a FREE variable, whose inequalities are exactly
/// what says which values it may take -- so a direct bind was never checked.
/// And an inequality with no variables left reached a simplex with no
/// columns, which has nothing to refute: {1 &gt; 3} succeeded on its own. The
/// disequality side had always decided its ground case, so only one half of
/// the store was enforcing itself.</para></summary>
public sealed class ClprInequalityVetoTests
{
    private static PrologEngine Clpr()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpr)).\n");
        return e;
    }

    private static bool Ask(string goal) => Clpr().Query(goal).Success;

    /// <summary>A ground inequality is a number to decide, not a store to
    /// grow. These never involved a variable at all.</summary>
    [Theory]
    [InlineData("{1 > 3}", false)]
    [InlineData("{3 > 1}", true)]
    [InlineData("{3 < 1}", false)]
    [InlineData("{1 < 3}", true)]
    [InlineData("{1 >= 3}", false)]
    [InlineData("{3 >= 3}", true)]
    [InlineData("{3 =< 1}", false)]
    [InlineData("{3 =< 3}", true)]
    public void GroundInequalitiesAreDecided(string goal, bool expected)
        => Assert.Equal(expected, Ask(goal + "."));

    /// <summary>The bug proper: unification is the other way a value
    /// reaches a constrained variable, and it was not consulting the
    /// store.</summary>
    [Theory]
    [InlineData("{X > 3}, X = 1.0", false)]
    [InlineData("{X > 3}, X = 5.0", true)]
    [InlineData("{X < 3}, X = 9.0", false)]
    [InlineData("{X < 3}, X = 1.0", true)]
    [InlineData("{X >= 3}, X = 3.0", true)]
    [InlineData("{X >= 3}, X = 2.0", false)]
    [InlineData("{X =\\= 3}, X = 3.0", false)]
    [InlineData("{X =\\= 3}, X = 4.0", true)]
    public void A_DirectBindIsVetted(string goal, bool expected)
        => Assert.Equal(expected, Ask(goal + "."));

    /// <summary>An integer is a real here: binding to 1 has to be refused by
    /// the same rule that refuses 1.0, or the veto has a hole the width of a
    /// type.</summary>
    [Fact]
    public void AnIntegerBindIsVettedToo()
    {
        Assert.False(Ask("{X > 3}, X = 1."));
        Assert.True(Ask("{X > 3}, X = 5."));
    }

    /// <summary>Posting onto an already-bound term: the constraint is ground
    /// by the time it is posted, which is the ground case reached from the
    /// other direction.</summary>
    [Fact]
    public void PostingOverABoundValueIsDecided()
    {
        Assert.False(Ask("X = 1, {X > 3}."));
        Assert.True(Ask("X = 5, {X > 3}."));
    }

    /// <summary>The veto must survive the variable being one of several: the
    /// row is only ground after the others are pinned, so this is the path
    /// through the wakeup rather than through the post.</summary>
    [Fact]
    public void TheVetoHoldsAcrossSeveralVariables()
    {
        Assert.False(Ask("{X > Y}, {Y = 5}, X = 1.0."));
        Assert.True(Ask("{X > Y}, {Y = 5}, X = 7.0."));
        Assert.False(Ask("{X > Y}, X = 1.0, {Y = 5}."));
    }

    /// <summary>The solver route always worked and must keep working: the
    /// fix is about the store being consulted, not about it being stricter.
    /// Without this, "veto everything" would pass the tests above.</summary>
    [Fact]
    public void ConsistentStoresStillSucceed()
    {
        Assert.True(Ask("{X > 3}, {X < 10}, X = 5.0."));
        Assert.False(Ask("{X > 3}, {X < 10}, X = 50.0."));
        Assert.True(Ask("{X > 3}, {X = 5}."));
        Assert.False(Ask("{X > 3}, {X = 1}."));
        Assert.True(Ask("{X + Y = 10}, {X > 3}, Y = 2.0."));
    }
}
