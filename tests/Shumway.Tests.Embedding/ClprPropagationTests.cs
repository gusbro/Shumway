using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A variable the store has DETERMINED comes back as a number, whichever post
/// determined it.
///
/// <para>The solver makes one variable of an equation dependent on the rest,
/// and the dependency lives only in that variable's own form. Nothing recorded
/// the other direction, so posting <c>{A+B =:= 10}</c> and later <c>{B =:= 4}</c>
/// left A determined but unbound: the answer was a residual where SWI and
/// SICStus give 6.0. It was never unsound (asking for a contradicting value
/// still failed) but it is not an answer anyone wants. A back-pointer on each
/// variable a form mentions closes it.</para>
/// </summary>
public class ClprPropagationTests
{
    private static PrologEngine Clpr()
    {
        var e = new PrologEngine();
        e.UseClpr();
        return e;
    }

    private static double Number(Term? t) => t switch
    {
        FloatTerm f => f.Value,
        IntTerm i => i.Value,
        _ => throw new Xunit.Sdk.XunitException(
            $"expected a number, got {t?.ToString() ?? "an unbound variable"}"),
    };

    [Theory]
    // The shape that was broken: the equation is posted first, the grounding
    // second.
    [InlineData("{A + B =:= 10}, {A =:= 6}")]
    // Its mirror: grounding the variable the OTHER one was made to depend on.
    [InlineData("{A + B =:= 10}, {B =:= 4}")]
    // The shapes that always worked, kept so a fix cannot regress them.
    [InlineData("{A =:= 6}, {A + B =:= 10}")]
    [InlineData("{A + B =:= 10, A =:= 6}")]
    [InlineData("{A + B =:= 10}, {A + 2 * B =:= 14}")]
    public void DeterminedVariablesComeBackAsNumbers(string goal)
    {
        var sol = Clpr().Query(goal + ".");
        Assert.True(sol.Success);
        Assert.Equal(6.0, Number(sol["A"]), 9);
        Assert.Equal(4.0, Number(sol["B"]), 9);
    }

    [Fact]
    public void PropagationFollowsAChain()
    {
        // X depends on Y depends on Z, and only Z is given.
        var sol = Clpr().Query("{X + Y =:= 10}, {Y + Z =:= 7}, {Z =:= 3}.");
        Assert.True(sol.Success);
        Assert.Equal(3.0, Number(sol["Z"]), 9);
        Assert.Equal(4.0, Number(sol["Y"]), 9);
        Assert.Equal(6.0, Number(sol["X"]), 9);
    }

    [Fact]
    public void TheClassicMortgageRunsBothWays()
    {
        // The reason the fix matters: every step of the recursion posts an
        // equation in two unknowns, and only the base case grounds one. Before
        // it, this answered with a residual in either direction.
        var e = Clpr();
        e.ConsultString("""
            mortgage(P, _, 0, B, _) :-
                {B =:= P}.
            mortgage(P, I, T, B, Pay) :-
                T > 0,
                T1 is T - 1,
                {P1 =:= P * (1 + I) - Pay},
                mortgage(P1, I, T1, B, Pay).
            """);
        var forward = e.Query("mortgage(100000, 0.01, 12, 0, Pay).");
        Assert.True(forward.Success);
        Assert.Equal(8884.88, Number(forward["Pay"]), 1);

        var backward = e.Query("mortgage(P, 0.01, 12, 0, 8884.878867834166).");
        Assert.True(backward.Success);
        Assert.Equal(100000.0, Number(backward["P"]), 1);
    }

    [Fact]
    public void AnUnderdeterminedVariableStaysAResidual()
    {
        // Binding what is determined must not turn into guessing. One equation
        // in two unknowns determines neither.
        var sol = Clpr().Query("{X + Y =:= 10}.");
        Assert.True(sol.Success);
        Assert.IsType<VarTerm>(sol["X"]);
        Assert.IsType<VarTerm>(sol["Y"]);
    }

    [Theory]
    // A value the store rules out is still refused, before and after the fix.
    [InlineData("{A + B =:= 10}, {A =:= 6}, {B =:= 5}")]
    [InlineData("{X > 5}, {X =:= 3}")]
    [InlineData("{X > 5}, {X + Y =:= 10}, {Y =:= 6}")]
    public void SoundnessIsNotTradedForPropagation(string goal)
    {
        Assert.False(Clpr().Query(goal + ".").Success);
    }
}
