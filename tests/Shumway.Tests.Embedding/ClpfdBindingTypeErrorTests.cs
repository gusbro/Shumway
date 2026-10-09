using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Binding a constrained variable to something that is not an
/// integer is a type error, as in SICStus and Scryer, and that includes a
/// unification that is only a test. It used to fail, so X \= b succeeded on a
/// variable that can never be b and member(X, [b, 1]) skipped the b in
/// silence.</summary>
public sealed class ClpfdBindingTypeErrorTests
{
    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).");
        return e;
    }

    [Theory]
    [InlineData("X = b", "b")]
    [InlineData("X = 1.0", "1.0")]
    [InlineData("X = f(a)", "f(a)")]
    [InlineData("X \\= b", "b")]
    [InlineData("member(X, [b, 1])", "b")]
    public void ANonIntegerIsATypeError(string bind, string culprit)
    {
        var e = Engine();
        Assert.True(e.Query($"catch((X in 0..1, {bind}), error(type_error(integer, V), _), true), V == {culprit}.").Success,
            $"{bind} did not raise type_error(integer, {culprit})");
    }

    [Theory]
    [InlineData("X = 1")]
    [InlineData("X = Y, Y = 1")]
    [InlineData("Y = X, var(Y)")]
    [InlineData("X = Y, Y in 1..5, X == Y")]
    public void IntegersAndVariablesBindAsBefore(string bind)
    {
        var e = Engine();
        Assert.True(e.Query($"X in 0..1, {bind}.").Success, $"{bind} failed");
    }

    [Fact]
    public void AnIntegerOutsideTheDomainStillFails()
    {
        var e = Engine();
        Assert.False(e.Query("catch((X in 0..1, X = 5), _, true).").Success);
    }
}
