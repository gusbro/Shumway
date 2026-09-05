using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A rational in the dynamic database (ADR-039). It has no source
/// literal, so the only way one reaches a clause is <c>assert</c> of a value
/// arithmetic computed, and that path had no encoding: every entry into the
/// database died on a host exception that <c>catch/3</c> never saw, taking
/// <c>retract/1</c> and <c>clause/2</c> with it.
///
/// <para>The encoding mirrors the big integers, two literal ids into the same
/// pool, so what these pin is that a rational goes in, comes back the number
/// it was, and is told apart from its neighbours.</para></summary>
public sealed class RationalDatabaseTests
{
    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(f/1).\n:- dynamic(g/2).\n");
        return e;
    }

    private static void Holds(PrologEngine e, string goal)
        => Assert.True(e.Query(goal).Success, goal);

    [Fact]
    public void AssertedAsAHeadArgument()
    {
        var e = Engine();
        Holds(e, "R is 1 rdiv 3, assertz(f(R)).");
        Holds(e, "f(X), R is 1 rdiv 3, X == R.");
    }

    [Fact]
    public void AssertedInsideACompound()
    {
        var e = Engine();
        Holds(e, "R is 1 rdiv 3, assertz(f(k(R, [R]))).");
        Holds(e, "f(k(X, [Y])), R is 1 rdiv 3, X == R, Y == R.");
    }

    [Fact]
    public void AssertedInABody()
    {
        var e = Engine();
        Holds(e, "R is 1 rdiv 3, assertz((h(X) :- X = R)).");
        Holds(e, "h(X), R is 1 rdiv 3, X == R.");
        // ...and as an operand of the body's arithmetic.
        Holds(e, "R is 1 rdiv 3, assertz((p(X) :- X is R + 1)).");
        Holds(e, "p(X), R is 4 rdiv 3, X == R.");
    }

    [Fact]
    public void AssertaPutsItFirst()
    {
        var e = Engine();
        Holds(e, "assertz(f(1)), R is 1 rdiv 3, asserta(f(R)).");
        Holds(e, "findall(X, f(X), [A, 1]), R is 1 rdiv 3, A == R.");
    }

    [Fact]
    public void RetractTakesTheOneItNames()
    {
        var e = Engine();
        Holds(e, "A is 1 rdiv 3, B is 2 rdiv 3, "
               + "assertz(f(A)), assertz(f(B)), assertz(f(1)).");
        Holds(e, "A is 1 rdiv 3, retract(f(A)).");
        Holds(e, "findall(X, f(X), [B, 1]), R is 2 rdiv 3, B == R.");
        // The one that is gone is gone.
        Assert.False(e.Query("A is 1 rdiv 3, f(A).").Success);
    }

    [Fact]
    public void ClauseGivesItBack()
    {
        var e = Engine();
        Holds(e, "R is 1 rdiv 3, assertz(f(R)).");
        Holds(e, "clause(f(X), true), R is 1 rdiv 3, X == R.");
    }

    [Fact]
    public void ItIsToldApartFromItsNeighbours()
    {
        // Dispatch on a rational first argument: the right clause, and no
        // clause at all for a value that is not there.
        var e = Engine();
        Holds(e, "A is 1 rdiv 3, B is 2 rdiv 3, "
               + "assertz(g(A, third)), assertz(g(B, twothirds)), assertz(g(1, one)).");
        Holds(e, "B is 2 rdiv 3, g(B, W), W == twothirds.");
        Holds(e, "A is 1 rdiv 3, g(A, W), W == third.");
        Assert.False(e.Query("C is 3 rdiv 4, g(C, _).").Success);
        // An integer is not the rational that reduces to it, and 2 rdiv 6 IS
        // 1 rdiv 3 -- canonical form, so it finds the same clause.
        Holds(e, "C is 2 rdiv 6, g(C, W), W == third.");
    }

    [Fact]
    public void NumeratorAndDenominatorOfAnySize()
    {
        var e = Engine();
        Holds(e, "R is 10^30 rdiv 7, assertz(f(R)).");
        Holds(e, "f(X), R is 10^30 rdiv 7, X == R.");
        Holds(e, "N is -1 rdiv 3, assertz(f(N)), f(Y), Y == N.");
    }

    [Fact]
    public void AndItIsStillANumberAfterwards()
    {
        var e = Engine();
        Holds(e, "R is 1 rdiv 3, assertz(f(R)).");
        Holds(e, "f(X), X =:= 1 rdiv 3, rational(X), number(X), \\+ integer(X).");
        Holds(e, "f(X), Y is X * 3, Y == 1.");
    }

    [Fact]
    public void ARepeatedlyCalledPredicateKeepsIt()
    {
        // The promotion machinery looks at a hot dynamic predicate (ADR-023).
        // A clause it cannot compile stays on Tier 0; what it must not do is
        // lose the value or fall over.
        var e = Engine();
        Holds(e, "R is 1 rdiv 3, assertz(g(k, R)), assertz(g(j, 1)).");
        Holds(e, "R is 1 rdiv 3, forall(between(1, 20000, _), (g(k, V), V == R)).");
        Holds(e, "findall(X-Y, g(X, Y), [k-A, j-1]), R is 1 rdiv 3, A == R.");
    }

    [Fact]
    public void ASavedDatabaseComesBackWithIt()
    {
        // save/1 writes the LIVE database through the term codec, which knew
        // integers and floats and not this.
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"rat_db_{System.Guid.NewGuid():N}.sav")
            .Replace('\\', '/');
        try
        {
            var e = Engine();
            Holds(e, "A is 1 rdiv 3, B is -10^30 rdiv 7, "
                   + "assertz(f(A)), assertz(f(B)), assertz(f(1)).");
            Holds(e, $"save('{path}').");

            var back = Engine();
            Holds(back, $"restore('{path}').");
            Holds(back, "findall(X, f(X), [A, B, 1]), "
                      + "A =:= 1 rdiv 3, B =:= -10^30 rdiv 7, rational(A), rational(B).");
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    [Fact]
    public void ItsTextIsTheTextItIsWrittenWith()
    {
        // number_chars/number_codes took anything that was not an int or a
        // float for a bignum and died on the cast.
        var e = new PrologEngine();
        Holds(e, "R is 1 rdiv 3, number_chars(R, Cs), atom_chars('1 rdiv 3', Cs).");
        Holds(e, "R is 1 rdiv 3, number_codes(R, Cs), atom_codes('1 rdiv 3', Cs).");
    }

    [Fact]
    public void ItReachesTheHostAsANumber()
    {
        var e = new PrologEngine();
        var sol = e.Query("X is 1 rdiv 3.");
        Assert.True(sol.Success);
        var r = Assert.IsType<RationalTerm>(sol["X"]);
        Assert.Equal(1, (int)r.Num);
        Assert.Equal(3, (int)r.Den);
        Assert.Equal(1.0 / 3.0, sol.Get<double>("X"), 12);
    }
}
