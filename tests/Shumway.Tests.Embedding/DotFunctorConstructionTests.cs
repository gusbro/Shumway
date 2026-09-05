using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>'.'/2</c> is the list constructor, so a term built with that
/// name and two arguments IS a cons, however it was built. The reader has
/// always known it: <c>X = '.'(1, [])</c> gives <c>[1]</c>. The two builtins
/// that CONSTRUCT a term from a name and an arity did not, so
/// <c>functor/3</c> and <c>=../2</c> produced a term that spelled a list,
/// printed as <c>.(1,[])</c>, and compared different from the list it spells.
/// The round trip <c>T =.. L, U =.. L</c> therefore did not give back the
/// term it started from.</summary>
public sealed class DotFunctorConstructionTests
{
    private static PrologEngine Engine() => new();

    [Fact]
    public void UnivRoundTripsAList()
    {
        // The defect in one goal: decomposing a list and composing the
        // result has to give the list back.
        Assert.True(Engine().Query("[1] =.. L, T =.. L, T == [1].").Success);
        Assert.True(Engine().Query(
            "[a,b,c] =.. L, T =.. L, T == [a,b,c].").Success);
    }

    [Theory]
    // Built by name, the term is a cons: every list test agrees.
    [InlineData("T =.. ['.', 1, []], is_list(T)")]
    [InlineData("T =.. ['.', 1, []], T == [1]")]
    [InlineData("T =.. ['.', 1, []], T = [_|_]")]
    [InlineData("T =.. ['.', a, foo], T = [a|foo]")]
    [InlineData("functor(T, '.', 2), T = [_|_]")]
    [InlineData("functor(T, '.', 2), T = [a|b], T == [a|b]")]
    public void ADotTermIsACons(string goal)
        => Assert.True(Engine().Query(goal + ".").Success);

    [Fact]
    public void ItIsTheSameTermTheReaderBuilds()
    {
        // Two spellings of one term. Nothing may tell them apart.
        Assert.True(Engine().Query(
            "X = '.'(1, []), Y =.. ['.', 1, []], X == Y, X = [1].").Success);
        Assert.True(Engine().Query(
            "functor([a|b], N, A), functor(T, N, A), T = [a|b].").Success);
    }

    [Fact]
    public void OtherFunctorsAreUnaffected()
    {
        Assert.True(Engine().Query("T =.. [f, 1, 2], T == f(1, 2).").Success);
        Assert.True(Engine().Query("functor(T, f, 2), T = f(_, _).").Success);
        // Arity two is what makes '.' the constructor; '.'/1 and '.'/3 are
        // ordinary compounds.
        Assert.True(Engine().Query(
            "T =.. ['.', 1], functor(T, '.', 1), \\+ is_list(T).").Success);
        Assert.True(Engine().Query(
            "T =.. ['.', 1, 2, 3], functor(T, '.', 3), \\+ is_list(T).").Success);
    }
}
