using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Exact rational numbers (ADR-039): the <c>rdiv</c> operator, the
/// <c>Tag.Rational</c> cell + side table, mixed rational arithmetic, the
/// <c>prefer_rationals</c> flag governing <c>/</c>, type tests, standard order,
/// and the writeq round-trip.
/// </summary>
public class RationalTests
{
    private static bool Holds(string goal) => new PrologEngine().Query(goal).Success;

    private static Term Bind(string goal, string var = "X")
    {
        var sol = new PrologEngine().Query(goal);
        Assert.True(sol.Success);
        return sol[var]!;
    }

    // ---- rdiv produces a rational; canonical form ----

    [Fact]
    public void Rdiv_ProducesRational()
    {
        var t = Bind("X is 1 rdiv 3.");
        var r = Assert.IsType<RationalTerm>(t);
        Assert.Equal(1, (int)r.Num);
        Assert.Equal(3, (int)r.Den);
    }

    [Fact]
    public void Rdiv_ReducesToCanonicalForm()
    {
        var r = Assert.IsType<RationalTerm>(Bind("X is 2 rdiv 4."));
        Assert.Equal(1, (int)r.Num);
        Assert.Equal(2, (int)r.Den);
    }

    [Fact]
    public void Rdiv_NegativeDenominatorNormalisesSign()
    {
        var r = Assert.IsType<RationalTerm>(Bind("X is 1 rdiv (-3)."));
        Assert.Equal(-1, (int)r.Num);
        Assert.Equal(3, (int)r.Den);   // denominator always positive
    }

    [Fact]
    public void Rdiv_IntegralResultCollapsesToInteger()
    {
        // 4 rdiv 2 = 2 — an integer, NOT a rational cell (canonical: den != 1).
        Assert.IsType<IntTerm>(Bind("X is 4 rdiv 2."));
        Assert.True(Holds("X is 4 rdiv 2, integer(X)."));
        Assert.False(Holds("X is 4 rdiv 2, X = 2, fail ; X is 4 rdiv 2, \\+ integer(X)."));
    }

    // ---- mixed arithmetic ----

    [Fact] public void Add_Rationals() => Assert.True(Holds("X is (1 rdiv 3) + (1 rdiv 6), X =:= 1 rdiv 2."));
    [Fact] public void Sub_Rationals() => Assert.True(Holds("X is (1 rdiv 3) - (1 rdiv 2), X =:= -1 rdiv 6."));
    [Fact] public void Mul_Rationals() => Assert.True(Holds("X is (2 rdiv 3) * (3 rdiv 4), X =:= 1 rdiv 2."));
    [Fact] public void Mul_RationalByInteger_CanCollapse() => Assert.True(Holds("X is (1 rdiv 3) * 3, integer(X), X =:= 1."));
    [Fact] public void RationalPlusInteger() => Assert.True(Holds("X is (1 rdiv 2) + 1, X =:= 3 rdiv 2."));
    [Fact] public void Negate_Rational() => Assert.True(Holds("X is -(1 rdiv 3), X =:= -1 rdiv 3."));
    [Fact] public void Abs_Rational() => Assert.True(Holds("X is abs(-1 rdiv 3), X =:= 1 rdiv 3."));

    // ---- rational + float floats the expression ----

    [Fact]
    public void RationalPlusFloat_Floats()
        => Assert.IsType<FloatTerm>(Bind("X is (1 rdiv 2) + 0.5."));

    // ---- comparison / standard order ----

    [Fact] public void Compare_EqualByValue() => Assert.True(Holds("1 rdiv 3 =:= 2 rdiv 6."));
    [Fact] public void Compare_Ordering() => Assert.True(Holds("1 rdiv 3 < 1 rdiv 2."));
    [Fact] public void Compare_RationalVsInteger() => Assert.True(Holds("1 rdiv 2 < 1."));
    [Fact] public void Compare_RationalVsFloat() => Assert.True(Holds("1 rdiv 2 =:= 0.5."));

    [Fact]
    public void StandardOrder_SortsRationalsByValue()
    {
        // Canonical variants deduplicate; sort orders by value.
        Assert.True(Holds(
            "A is 1 rdiv 4, B is 1 rdiv 2, C is 1 rdiv 3, "
            + "sort([B, A, C], S), S = [X4, X3, X2], "
            + "X4 =:= 1 rdiv 4, X3 =:= 1 rdiv 3, X2 =:= 1 rdiv 2."));
    }

    // ---- structural equality ----

    [Fact]
    public void StructuralEquality_OfEqualRationals()
        => Assert.True(Holds("X is 1 rdiv 3, Y is 2 rdiv 6, X == Y."));

    [Fact]
    public void StructuralInequality_OfDifferentRationals()
        => Assert.True(Holds("X is 1 rdiv 3, Y is 1 rdiv 2, X \\== Y."));

    // ---- type tests ----

    [Fact] public void Rational_TrueForRational() => Assert.True(Holds("X is 1 rdiv 3, rational(X)."));
    [Fact] public void Rational_TrueForInteger() => Assert.True(Holds("rational(5)."));
    [Fact] public void Number_TrueForRational() => Assert.True(Holds("X is 1 rdiv 3, number(X)."));
    [Fact] public void Atomic_TrueForRational() => Assert.True(Holds("X is 1 rdiv 3, atomic(X)."));
    [Fact] public void Integer_FalseForRational() => Assert.False(Holds("X is 1 rdiv 3, integer(X)."));
    [Fact] public void Float_FalseForRational() => Assert.False(Holds("X is 1 rdiv 3, float(X)."));
    [Fact] public void Compound_FalseForRational() => Assert.False(Holds("X is 1 rdiv 3, compound(X)."));
    [Fact] public void Ground_TrueForRational() => Assert.True(Holds("X is 1 rdiv 3, ground(X)."));

    // ---- numerator / denominator / rationalize ----

    [Fact] public void Numerator() => Assert.True(Holds("X is numerator(3 rdiv 7), X == 3."));
    [Fact] public void Denominator() => Assert.True(Holds("X is denominator(3 rdiv 7), X == 7."));
    [Fact] public void Rationalize_OfHalf() => Assert.True(Holds("X is rationalize(0.5), X =:= 1 rdiv 2."));

    // ---- the prefer_rationals flag governing '/' ----

    [Fact]
    public void SlashDefault_IsFloat()
        => Assert.IsType<FloatTerm>(Bind("X is 1/3."));

    [Fact]
    public void SlashUnderFlag_IsRational()
        => Assert.IsType<RationalTerm>(Bind("set_prolog_flag(prefer_rationals, true), X is 1/3."));

    [Fact]
    public void SlashUnderFlag_ExactQuotientStaysInteger()
        // 4/2 under the flag: rational division of 4/2 = 2 (an integer, not 2.0).
        => Assert.IsType<IntTerm>(Bind("set_prolog_flag(prefer_rationals, true), X is 4/2."));

    [Fact]
    public void FlagDefaultsFalse()
        => Assert.True(Holds("current_prolog_flag(prefer_rationals, false)."));

    [Fact]
    public void FlagReadsBackTrue()
        => Assert.True(Holds("set_prolog_flag(prefer_rationals, true), current_prolog_flag(prefer_rationals, true)."));

    // ---- writeq round-trip ----

    [Fact]
    public void Writeq_RendersRdivOperator()
    {
        Assert.True(Holds("X is 7 rdiv 2, term_to_atom(X, A), A == '7 rdiv 2'."));
    }

    [Fact]
    public void Writeq_RationalReReadsToSameValue()
    {
        // Render then read back through term parsing + evaluation.
        Assert.True(Holds(
            "X is 3 rdiv 5, term_to_atom(X, A), term_to_atom(T, A), Y is T, X =:= Y."));
    }

    // ---- backtrack reclaim: a failure-driven loop building many rationals
    //      must not corrupt the side table nor leak a stale id ----

    [Fact]
    public void BacktrackReclaim_LoopOfRationals()
    {
        Assert.True(Holds(
            "( between(1, 2000, N), M is 1 rdiv (N + 1), \\+ integer(M), fail ; true ), "
            + "Final is 1 rdiv 7, Final =:= 1 rdiv 7."));
    }

    // ---- copy_term / findall carry the rational across the boundary ----

    [Fact]
    public void CopyTerm_PreservesRational()
        => Assert.True(Holds("X is 1 rdiv 3, copy_term(X, Y), X == Y."));

    [Fact]
    public void Findall_CollectsRationals()
        => Assert.True(Holds(
            "findall(R, (member(N, [2, 3, 4]), R is 1 rdiv N), Rs), "
            + "Rs = [A, B, C], A =:= 1 rdiv 2, B =:= 1 rdiv 3, C =:= 1 rdiv 4."));

    // ---- C# flag API round-trips through the engine ----

    [Fact]
    public void CSharp_FlagDefaultFalse()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("X is 1/3, float(X).").Success);
    }
}
