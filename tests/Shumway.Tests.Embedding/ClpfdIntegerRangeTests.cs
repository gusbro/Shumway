using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The integers of library(clpfd): the inline ones, -576460752303423488 to
/// 576460752303423487, with <c>inf</c> and <c>sup</c> for a side that has no
/// bound.
///
/// <para>Nothing past that range is ever a wrong answer or an exception of
/// the host. An integer past it written in a constraint is
/// <c>representation_error(max_clpfd_integer)</c> or
/// <c>min_clpfd_integer</c>. A bound computed past it leaves its side
/// unbounded, and what a narrowing leaves of an unbounded side past the
/// range is the same error: failing there would deny a constraint that has
/// solutions.</para>
/// </summary>
public sealed class ClpfdIntegerRangeTests
{
    private const string Max = "576460752303423487";
    private const string Min = "-576460752303423488";
    private const string TooBig = "representation_error(max_clpfd_integer)";
    private const string TooSmall = "representation_error(min_clpfd_integer)";

    private const string Factorial = """
        n_factorial(0, 1).
        n_factorial(N, F) :- N #> 0, N1 #= N - 1, F #= N * F1, n_factorial(N1, F1).
        """;

    private static PrologEngine Fd(string? program = null)
    {
        var e = new PrologEngine();
        e.UseClpfd();
        if (program is not null) e.ConsultString(program);
        return e;
    }

    private static string Text(PrologEngine e, Shumway.Compiler.Ast.Term t) =>
        AstTermRenderer.Render(t, 4000, e.Operators);

    // "true", "false", or the error the goal raises.
    private static string Outcome(string goal, string? program = null)
    {
        var e = Fd(program);
        var s = e.Query(
            $"catch((({goal}) -> R__ = true ; R__ = false), error(E__, _), R__ = E__).");
        Assert.True(s.Success, goal);
        return Text(e, s["R__"]!);
    }

    // The domain of Var after the goal, as `in` would be given it.
    private static string Domain(string goal, string var)
    {
        var e = Fd();
        var s = e.Query($"{goal}, copy_term({var}, {var}C__, Gs__), Gs__ = [{var}C__ in D__|_].");
        Assert.True(s.Success, goal);
        return Text(e, s["D__"]!).Replace(" ", "");
    }

    // The residual goals of Vars after the goal.
    private static string Residue(string goal, string vars)
    {
        var e = Fd();
        var s = e.Query($"{goal}, copy_term({vars}, _, Gs__).");
        Assert.True(s.Success, goal);
        return Text(e, s["Gs__"]!).Replace(" ", "");
    }

    [Fact]
    public void AFactorialPastTheRange_IsARepresentationError()
    {
        var e = Fd(Factorial);
        // ANTI-VACUITY: the relation runs, and 19 is the last one that fits.
        Assert.Equal("121645100408832000", Text(e, e.Query("n_factorial(19, F).")["F"]!));
        Assert.Equal("5", Text(e, e.Query("n_factorial(N, 120).")["N"]!));
        Assert.Equal(TooBig, Outcome("n_factorial(20, _)", Factorial));
        Assert.Equal(TooBig, Outcome("n_factorial(25, _)", Factorial));
    }

    [Theory]
    [InlineData("X #=< 100000000000000000000", TooBig)]
    [InlineData("X #>= -100000000000000000000", TooSmall)]
    [InlineData("X #= 100000000000000000000", TooBig)]
    [InlineData("X in 0..10, X #= 100000000000000000000", TooBig)]
    [InlineData("X in 0..10, X #\\= 100000000000000000000", TooBig)]
    [InlineData("X in 0..10, Y #= 1000000000000000000000 * X", TooBig)]
    [InlineData("X in 0..10, 2000000000000000000 * X #\\= 3", TooBig)]
    [InlineData("X in 0..10, Y in 0..10, 2*X + 3*Y #=< 100000000000000000000", TooBig)]
    [InlineData("X in 0..576460752303423488", TooBig)]
    [InlineData("X in -576460752303423489..0", TooSmall)]
    [InlineData("[X, Y] ins 0..100000000000000000000", TooBig)]
    [InlineData("sum([400000000000000000000, X], #=, 5)", TooBig)]
    public void AnIntegerPastTheRangeInAConstraint_IsRefused(string goal, string error)
        => Assert.Equal(error, Outcome(goal));

    [Fact]
    public void TheEndsOfTheRange_AreInIt()
    {
        Assert.Equal($"{Min}..{Max}", Domain($"X in {Min}..{Max}", "X"));
        Assert.Equal("true", Outcome($"X in {Min}..{Max}, X = {Max}"));
        Assert.Equal("true", Outcome($"X in {Min}..{Max}, X = {Min}"));
        Assert.Equal($"{Max}..sup", Domain($"X #>= {Max}", "X"));
        Assert.Equal($"inf..{Min}", Domain($"X #=< {Min}", "X"));
        Assert.Equal("true", Outcome($"X in 576460752303423485..{Max}, label([X]), X =:= 576460752303423485"));
    }

    [Theory]
    [InlineData("X in 1..1000000000, Y in 1..1000000000000, Z #= X*Y", "1..sup")]
    [InlineData("X in 0..4000000000, Y in 0..4000000000, Z #= X*Y", "0..sup")]
    [InlineData("X in 0..500000000000000000, Y in 0..500000000000000000, Z #= X + Y", "0..sup")]
    [InlineData("X in -500000000000000000..0, Y in 0..500000000000000000, Z #= X - Y", "inf..0")]
    [InlineData("X in -576460752303423488..0, Z #= -X", "0..sup")]
    [InlineData("X in 0..576460752303423487, Z #= 2 * X", "0..sup")]
    [InlineData("X in 0..100, Y in 0..100, 400000000000000000 * X + 3 * Y #= Z", "0..sup")]
    public void ABoundComputedPastTheRange_LeavesItsSideUnbounded(string goal, string domain)
        => Assert.Equal(domain, Domain(goal, "Z"));

    // 2^58 * 128 is 2^65, which a machine word holds as 0: the product's
    // upper bound came out 0 and X was pruned to it.
    [Fact]
    public void AProductThatWrapsAMachineWord_PrunesNothing()
    {
        const string goal = "X in 0..288230376151711744, Z #= 128 * X";
        Assert.Equal("0..288230376151711744", Domain(goal, "X"));
        Assert.Equal("0..sup", Domain(goal, "Z"));
        Assert.Equal("true", Outcome($"{goal}, X = 5, Z =:= 640"));
        // -2^59 * 16 is the least machine word exactly, and still a number.
        Assert.Equal("true", Outcome("clpfd_bmul(-576460752303423488, 16, R), R =:= -(2^63)"));
    }

    [Theory]
    [InlineData("X in 1000000000000..10000000000000, Z #= X*X", TooBig)]
    [InlineData("X in 1000000000000..10000000000000, Z in 0..100, Z #= X*X", TooBig)]   // the product's own variable has no bound yet
    [InlineData("X = 500000000000000000, Y = 500000000000000000, Z #= X + Y", TooBig)]
    [InlineData("X = -500000000000000000, Y = 500000000000000000, Z #= X - Y", TooSmall)]
    [InlineData("X = 3000000000, Z #= X * X", TooBig)]
    [InlineData("X in 0..3000000, Z #= X*X*X, X = 3000000", TooBig)]
    [InlineData("X = -576460752303423488, Z #= abs(X)", TooBig)]
    [InlineData("X = -576460752303423488, Z #= 0 - X", TooBig)]
    [InlineData("X = 100, Z #= X ** 12", TooBig)]
    [InlineData("X #> 576460752303423487", TooBig)]
    [InlineData("X #< -576460752303423488", TooSmall)]
    [InlineData("X in 0..10, X #> 576460752303423487", "false")]
    [InlineData("X in 0..10, X #< -576460752303423488", "false")]
    public void WhatIsLeftOnlyPastTheRange_IsAnErrorOnAnUnboundedSideAndFailsOnABoundedOne(
        string goal, string outcome)
        => Assert.Equal(outcome, Outcome(goal));

    [Theory]
    [InlineData("X #> 0, X = 100000000000000000000", TooBig)]
    [InlineData("X #< 0, X = -100000000000000000000", TooSmall)]
    [InlineData("X in 0..10, X = 100000000000000000000", "false")]
    [InlineData("X #> 0, X = -100000000000000000000", "false")]
    public void BindingAConstrainedVariableToAnIntegerPastTheRange(string goal, string outcome)
        => Assert.Equal(outcome, Outcome(goal));

    // The value one past an end of the range has no bound to be written
    // with, so on an unbounded side the end stays in the domain and the
    // disequality stays in the answer.
    [Theory]
    [InlineData("X #\\= -576460752303423488", "#\\=-576460752303423488")]
    [InlineData("X #\\= 576460752303423487", "#\\=576460752303423487")]
    [InlineData("X #\\= Y, Y = 576460752303423487", "#\\=576460752303423487")]
    [InlineData("X + Y #\\= -576460752303423488, Y = 0", "#\\=")]
    public void ADisequalityAgainstAnEndOfTheRange_IsSaidByTheAnswer(string goal, string says)
        => Assert.Contains(says, Residue(goal, "X"));

    [Theory]
    [InlineData("X #\\= 576460752303423487, X = 576460752303423487", "false")]
    [InlineData("X #\\= -576460752303423488, X = -576460752303423488", "false")]
    [InlineData("X #\\= -576460752303423488, X = -5", "true")]
    [InlineData("X + Y #\\= -576460752303423488, Y = 0, X = -576460752303423488", "false")]
    [InlineData("X + Y #\\= -576460752303423488, Y = 0, X = 7", "true")]
    public void ADisequalityAgainstAnEndOfTheRange_Holds(string goal, string outcome)
        => Assert.Equal(outcome, Outcome(goal));

    [Fact]
    public void OnceTheSideIsBounded_TheEndLeavesTheDomain()
    {
        Assert.Equal("-576460752303423487..sup",
            Domain($"X #\\= {Min}, X #>= {Min}", "X"));
        Assert.Equal("inf..576460752303423486",
            Domain($"X #\\= {Max}, X #=< {Max}", "X"));
        // ANTI-VACUITY: an ordinary value leaves at once.
        Assert.Equal("inf..4\\/6..sup", Domain("X #\\= 5", "X"));
        Assert.DoesNotContain("#\\=", Residue("X #\\= 5", "X"));
    }

    [Theory]
    [InlineData("C = 576460752303423487", "false")]
    [InlineData("C = 576460752303423486", "false")]
    [InlineData("C = 576460752303423485", "true")]
    [InlineData("C #=< 576460752303423487, C = 576460752303423487", "false")]
    public void AllDistinctAtTheTopOfTheRange(string then, string outcome)
        => Assert.Equal(outcome, Outcome(
            "all_distinct([A, B, C]), A in 576460752303423486..576460752303423487, "
            + "B in 576460752303423486..576460752303423487, " + then));

    [Theory]
    [InlineData("C = -576460752303423488", "false")]
    [InlineData("C = -576460752303423486", "true")]
    public void AllDistinctAtTheBottomOfTheRange(string then, string outcome)
        => Assert.Equal(outcome, Outcome(
            "all_distinct([A, B, C]), A in -576460752303423488.. -576460752303423487, "
            + "B in -576460752303423488.. -576460752303423487, " + then));

    [Fact]
    public void TheSizeOfTheWholeRange_IsExact()
        => Assert.Equal("true", Outcome(
            $"'$dom_new'({Min}, {Max}, D), '$dom_size'(D, N), N =:= 2^60"));

    // The bound primitives against the engine's own arithmetic, on operands
    // and results past the inline range.
    [Theory]
    [InlineData("clpfd_add_hi(576460752303423487, 576460752303423487, R), R =:= 2 * 576460752303423487")]
    [InlineData("clpfd_add_lo(-576460752303423488, -576460752303423488, R), R =:= -(2^60)")]
    [InlineData("clpfd_sub_lo(-576460752303423488, 576460752303423487, R), R =:= -576460752303423488 - 576460752303423487")]
    [InlineData("clpfd_sub_hi(576460752303423487, -576460752303423488, R), R =:= 2^60 - 1")]
    [InlineData("clpfd_bneg(-576460752303423488, R), R =:= 2^59")]
    [InlineData("clpfd_bmul(576460752303423487, 576460752303423487, R), R =:= 576460752303423487 * 576460752303423487")]
    [InlineData("clpfd_bmul(-4294967296, 4294967296, R), R =:= -(2^64)")]
    [InlineData("clpfd_bmul(3000000000, -3, R), R =:= -9000000000")]
    [InlineData("clpfd_bfloordiv(-576460752303423488, -1, R), R =:= 2^59")]
    [InlineData("clpfd_bceildiv(-576460752303423488, -1, R), R =:= 2^59")]
    [InlineData("X is 7 - 2^70, clpfd_bfloordiv(X, 3, R), R =:= X div 3")]
    [InlineData("X is 7 - 2^70, clpfd_bceildiv(X, 3, R), R =:= -((-X) div 3)")]
    [InlineData("X is 2^70 + 1, clpfd_bfloordiv(X, -3, R), R =:= X div -3")]
    [InlineData("X is 2^70 + 1, clpfd_bceildiv(X, -3, R), R =:= -(X div 3)")]
    [InlineData("X is 2^70, clpfd_bfloordiv(X, 2, R), R =:= 2^69")]
    [InlineData("X is 2^70, K is 2^65, clpfd_bmul(X, K, R), R =:= 2^135")]
    [InlineData("X is 2^70, clpfd_add_lo(X, -5, R), R =:= X - 5")]
    [InlineData("X is 2^70, clpfd_sub_hi(5, X, R), R =:= 5 - X")]
    [InlineData("X is 2^70, clpfd_bneg(X, R), R =:= -X")]
    [InlineData("X is 2^70, clpfd_ble(576460752303423487, X), clpfd_blt(5, X), \\+ clpfd_ble(X, 5)")]
    [InlineData("X is -(2^70), clpfd_blt(X, -576460752303423488), clpfd_blt(inf, X), clpfd_blt(X, sup)")]
    [InlineData("X is 2^70, clpfd_bmin(X, 5, 5), clpfd_bmax(X, 5, M), M == X, clpfd_bmax(X, sup, sup)")]
    [InlineData("X is 2^70, clpfd_bmul(inf, X, inf), clpfd_bmul(sup, X, sup), Y is -X, clpfd_bmul(inf, Y, sup)")]
    [InlineData("X is 2^70, clpfd_add_lo(inf, X, inf), clpfd_add_hi(X, sup, sup), clpfd_sub_lo(X, sup, inf)")]
    [InlineData("clpfd_bmul(inf, -3, sup), clpfd_bmul(sup, 3, sup), clpfd_bfloordiv(inf, -2, sup), clpfd_bceildiv(sup, 2, sup)")]
    [InlineData("clpfd_add_lo(inf, 5, inf), clpfd_add_hi(5, sup, sup), clpfd_sub_lo(5, sup, inf), clpfd_sub_hi(5, inf, sup)")]
    [InlineData("clpfd_add_lo(3, 4, 7), clpfd_sub_hi(3, 4, -1), clpfd_bneg(7, -7), clpfd_bmul(-6, 7, -42)")]
    [InlineData("clpfd_bfloordiv(-7, 2, -4), clpfd_bceildiv(-7, 2, -3), clpfd_bfloordiv(7, 2, 3), clpfd_bceildiv(7, 2, 4)")]
    public void BoundArithmeticIsExact(string goal)
        => Assert.Equal("true", Outcome(goal));

    [Theory]
    [InlineData("clpfd_bfloordiv(5, 0, _)", "evaluation_error(zero_divisor)")]
    [InlineData("X is 2^70, clpfd_bceildiv(X, 0, _)", "evaluation_error(zero_divisor)")]
    public void DividingABoundByZero_IsThePrologError(string goal, string error)
        => Assert.Equal(error, Outcome(goal));
}
