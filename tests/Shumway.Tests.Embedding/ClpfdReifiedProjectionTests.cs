using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What a constrained answer says about reified constraints and
/// products, checked against SICStus (with full_answer) and Scryer.
///
/// <para>A reified comparison printed nothing: <c>B #&lt;==&gt; (X #&gt; 5)</c>
/// answered with the two domains only, and an implication left an auxiliary
/// truth value the answer named with no constraint on it, an answer that
/// says less than what holds. A product of two variables printed twice when
/// a variable occurred in it twice, and narrowed nothing when a bound was
/// inf or sup.</para></summary>
public sealed class ClpfdReifiedProjectionTests
{
    private static PrologEngine Fd()
    {
        var e = new PrologEngine();
        e.UseClpfd();
        return e;
    }

    // The residual goals of Vars, rendered, with variables named as written.
    private static string Residue(string goal, string vars)
    {
        var e = Fd();
        var s = e.Query($"{goal}, copy_term({vars}, _, Gs__).");
        Assert.True(s.Success, goal);
        return AstTermRenderer.Render(s["Gs__"]!, 4000, e.Operators).Replace(" ", "");
    }

    private static int Count(string text, string part)
    {
        int n = 0;
        for (int i = text.IndexOf(part, System.StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(part, i + part.Length, System.StringComparison.Ordinal))
            n++;
        return n;
    }

    private static string Domain(string goal, string var)
    {
        var e = Fd();
        var s = e.Query($"{goal}, copy_term({var}, {var}C__, Gs__), Gs__ = [{var}C__ in D__|_].");
        Assert.True(s.Success, goal);
        return AstTermRenderer.Render(s["D__"]!, 1200, e.Operators).Replace(" ", "");
    }

    [Theory]
    [InlineData("B #<==> (X #> 5)", "#>5#<==>")]
    [InlineData("X in 0..10, B #<==> (X #= Y)", "#=")]
    [InlineData("B #<==> (X #= 3), X in 0..9", "#=3#<==>")]
    [InlineData(@"X in 0..9, B #<==> (X #\= 4)", @"#\=4#<==>")]
    [InlineData("X in 0..9, (X #< 3) #==> B", "#<3#<==>")]
    [InlineData("X in 0..9, B #==> (X #< 3)", "#<3#<==>")]
    public void AnOpenReification_IsInTheAnswer(string goal, string says)
    {
        string r = Residue(goal, "[B,X,Y]");
        Assert.Contains(says, r);
        Assert.Contains("#<==>", r);
    }

    // ANTI-VACUITY: the residue is not the domains alone.
    [Fact]
    public void AnOpenReification_SaysMoreThanTheDomains()
        => Assert.Equal(2, Count(Residue("B #<==> (X #> 5)", "[B,X]"), ","));   // three goals; the domains alone are two

    [Theory]
    [InlineData("X in 0..9, Y in 0..9, B #<==> (X #< Y), B = 1", "#<_")]
    [InlineData("X in 0..9, Y in 0..9, B #<==> (X #< Y), B = 0", "#=<_")]
    [InlineData("X in 0..9, Y in 0..9, B #<==> (X #= Y), B = 0", @"#\=_")]
    public void ADecidedReification_IsTheConstraintItEnforces(string goal, string says)
    {
        string r = Residue(goal, "[X,Y]");
        Assert.Contains(says, r);
        Assert.DoesNotContain("#<==>", r);
    }

    [Theory]
    [InlineData("X in 0..9, B #<==> (X #= 3), B = 0", "[X]", @"0..2\/4..9")]
    [InlineData("X in 0..9, B #<==> (X #> 5), B = 1", "[X]", "6..9")]
    public void ADecidedReificationAgainstAnInteger_IsSaidByTheDomain(string goal, string vars, string domain)
    {
        string r = Residue(goal, vars);
        Assert.Contains(domain, r);
        Assert.DoesNotContain("#", r.Replace("#<==>", "").Replace("in", ""));
    }

    // A disjunction is one linear constraint over its two truth values, not
    // a sum held by a variable the user never wrote.
    [Fact]
    public void ADisjunction_NamesNoAuxiliarySum()
    {
        string r = Residue("X in 0..9, Y in 0..9, (X #= 1) #\\/ (Y #= 2)", "[X,Y]");
        Assert.Contains("#>=1", r);
        Assert.Equal(2, Count(r, "#<==>"));
        Assert.Equal(1, Count(r, "#>=1"));
    }

    [Theory]
    [InlineData(@"X in 0..9, Y in 0..9, (X #= 1) #\/ (Y #= 2), X = 3", true)]
    [InlineData(@"X in 0..9, Y in 0..9, (X #= 1) #\/ (Y #= 2), X = 3, Y = 2", true)]
    [InlineData(@"X in 0..9, Y in 0..9, (X #= 1) #\/ (Y #= 2), X = 3, Y = 4", false)]
    [InlineData(@"X in 0..9, Y in 0..9, (X #= 1) #\/ (Y #= 2), X = 1, Y = 4", true)]
    public void ADisjunctionHolds(string goal, bool holds)
        => Assert.Equal(holds, Fd().Query(goal + ".").Success);

    [Fact]
    public void ADisjunctionPropagates()
        => Assert.True(Fd().Query("X in 0..9, Y in 0..9, (X #= 1) #\\/ (Y #= 2), X = 3, Y == 2.").Success);

    [Theory]
    [InlineData("X in 0..100, Y #= X*X")]
    [InlineData("X #> 0, Y #= X*X")]
    [InlineData("X*Z #= Y, X = Z")]
    public void AProductIsSaidOnce(string goal)
        => Assert.Equal(1, Count(Residue(goal, "[X,Y]"), "#="));

    [Theory]
    [InlineData("X #> 0, Y #= X*X", "Y", "1..sup")]
    [InlineData("X in -5..5, Y #= X*X", "Y", "0..25")]
    [InlineData("X in -5..-2, Y #= X*X", "Y", "4..25")]
    [InlineData("X #< 0, Y #= X*X", "Y", "1..sup")]
    [InlineData("X*Z #= Y, X = Z", "Y", "0..sup")]
    [InlineData("X in 1..10, Z #> 0, Y #= X*Z", "Y", "1..sup")]
    [InlineData("X in -3..2, Z in 2..sup, Y #= X*Z", "Y", "inf..sup")]
    [InlineData("X in 2..3, Z #=< -1, Y #= X*Z", "Y", "inf..-2")]
    [InlineData("X in 0..3, Z #> 5, Y #= X*Z", "Y", "0..sup")]
    [InlineData("X in 0..100, Y #= X**12", "Y", "0..sup")]
    public void AProductOfTwoVariables_IsBoundedWithInfiniteSidesToo(string goal, string var, string domain)
        => Assert.Equal(domain, Domain(goal, var));

    [Theory]
    [InlineData("clpfd_bxmul(0, sup, 0), clpfd_bxmul(inf, 0, 0)")]
    [InlineData("clpfd_bxmul(inf, inf, sup), clpfd_bxmul(inf, sup, inf), clpfd_bxmul(sup, -3, inf)")]
    [InlineData("clpfd_bxmul(-4, inf, sup), clpfd_bxmul(6, -7, -42)")]
    [InlineData("clpfd_bxmul(576460752303423487, 576460752303423487, R), R =:= 576460752303423487^2")]
    [InlineData("X is 2^70, clpfd_bxmul(X, -1, R), R =:= -X, clpfd_bxmul(X, sup, sup), clpfd_bxmul(X, 0, 0)")]
    public void TheCornerProductOfTwoBounds(string goal)
        => Assert.True(Fd().Query(goal + ".").Success, goal);
}
