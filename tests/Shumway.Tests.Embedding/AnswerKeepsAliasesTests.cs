using System.IO;
using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An answer says which query variables are the same variable, a
/// constrained one included, and a `_` variable it does not show never
/// stands between two it does.
///
/// <para>The equalities came from grouping the query variables by value, and
/// a variable with residual goals was left out of the grouping. With
/// <c>X in 0..9, Y = X</c> the residual named X, Y was left alone in its
/// group, and a lone unbound variable is not shown: the answer was
/// <c>X in 0..9</c>, which says nothing about Y. A chain through a `_` name
/// stopped there: <c>X = _T, Z = _T</c> answered <c>X = _T</c>. SICStus and
/// Scryer show the equalities first, then the residual goals.</para></summary>
public sealed class AnswerKeepsAliasesTests
{
    private static PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.UseClpfd();
        e.UseCoroutining();
        return e;
    }

    private static string Answer(string query)
    {
        using var run = new TopLevelSession(Engine()).StartQuery(query);
        Assert.True(run.MoveNext(), $"no solution for {query}");
        return run.Format(200).Replace("\r\n", "\n");
    }

    [Theory]
    [InlineData("X in 0..9, Y in 0..9, X = Y.", "X = Y,\nX in 0..9")]
    [InlineData("X in 0..9, Y = X.", "X = Y,\nX in 0..9")]
    [InlineData("X in 0..9, Y = X, Z = Y.", "X = Y,\nY = Z,\nX in 0..9")]
    [InlineData("X in 0..9, Y in 0..9, Z in 0..9, X = Z.", "X = Z,\nX in 0..9,\nY in 0..9")]
    [InlineData("X in 0..9, Y = X, W = f(Y).", "X = Y,\nX in 0..9,\nW = f(X)")]
    [InlineData("freeze(X, true), Y = X.", "X = Y,\nfreeze(X, true)")]
    [InlineData("dif(X, a), Y = X.", "X = Y,\ndif(X, a)")]
    [InlineData("X in 0..9, Y = X, Y = 3.", "X = Y,\nY = 3")]
    public void AConstrainedVariable_KeepsItsAliases(string query, string answer)
        => Assert.Equal(answer, Answer(query));

    [Fact]
    public void AnAliasOfAProductsVariable_IsInTheAnswer()
    {
        string a = Answer("X #> 0, Y #= X*X, Z = Y.");
        Assert.Contains("Y = Z", a);
        Assert.Contains("X in 1..sup", a);
        Assert.Contains("Y in 1..sup", a);
    }

    [Theory]
    [InlineData("X = f(a), _T = f(a).", "X = f(a)")]
    [InlineData("X = _T, Z = _T.", "X = Z")]
    [InlineData("_T = X, Z = X.", "X = Z")]
    [InlineData("X = _T, _T = Z.", "X = Z")]
    [InlineData("X in 0..9, _T = X.", "X in 0..9")]
    // The residual takes the name the answer shows, not the hidden one.
    [InlineData("_T in 0..9, X = _T.", "X in 0..9")]
    public void AHiddenVariable_NeverStandsBetweenShownOnes(string query, string answer)
        => Assert.Equal(answer, Answer(query));

    // ANTI-VACUITY: what was right before stays as it was.
    [Theory]
    [InlineData("X = Y.", "X = Y")]
    [InlineData("X = Y, Y = Z.", "X = Y,\nY = Z")]
    [InlineData("X = f(a), Y = f(a).", "X = Y,\nY = f(a)")]
    [InlineData("X = f(Y), Y in 0..9.", "X = f(Y),\nY in 0..9")]
    [InlineData("X in 0..9.", "X in 0..9")]
    [InlineData("X = Y, _ = Z.", "X = Y")]
    public void WhatWasRightStaysRight(string query, string answer)
        => Assert.Equal(answer, Answer(query));
}
