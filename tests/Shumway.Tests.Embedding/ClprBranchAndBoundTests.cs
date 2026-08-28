using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <c>bb_inf/3,4</c>: the infimum with some variables restricted to integers.
///
/// <para>This is what the simplex bought. Branch and bound has to know WHICH
/// integer variable came out fractional and where to split it, which means
/// reading the values at the relaxed optimum; Fourier-Motzkin gives bounds and
/// no point, so the predicate could not be written over it at all.</para>
/// </summary>
public class ClprBranchAndBoundTests
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
        _ => throw new Xunit.Sdk.XunitException($"not a number: {t}"),
    };

    [Fact]
    public void IntegralityMovesTheBound()
    {
        var e = Clpr();
        // Relaxed, the infimum is 0.5; over the integers it is 1.
        Assert.Equal(0.5, Number(e.Query("{X >= 0.5, X =< 3.4}, inf(X, V).")["V"]), 9);
        Assert.Equal(1.0, Number(e.Query("{X >= 0.5, X =< 3.4}, bb_inf([X], X, V).")["V"]), 9);
        // And from the other side: the largest integer in [1.2, 4.8] is 4.
        Assert.Equal(-4.0, Number(e.Query("{X >= 1.2, X =< 4.8}, bb_inf([X], -X, V).")["V"]), 9);
    }

    [Fact]
    public void TheVertexNamesWhereItHappens()
    {
        var e = Clpr();
        // Checked as VALUES, not as rendered text: how a list of floats
        // prints is a different subject from where the optimum sits.
        var sol = e.Query(
            "{X + Y =:= 5, X >= 0, Y >= 0}, bb_inf([X, Y], X, V, Vertex), "
            + "Vertex = [Xv, Yv], Xv =:= 0, Yv =:= 5.");
        Assert.True(sol.Success);
        Assert.Equal(0.0, Number(sol["V"]), 9);
    }

    [Theory]
    // 2x + 3y >= 12 with both in [0, 10]: the cheapest integer point is (0, 4).
    [InlineData("{2*X + 3*Y >= 12, X >= 0, Y >= 0, X =< 10, Y =< 10}, "
                + "bb_inf([X, Y], X + Y, V)", 4.0)]
    // Maximise x + y (as minimising its negation) under a diagonal cut.
    [InlineData("{X >= 1.5, X =< 9.5, Y >= 0.5, Y =< 4.5, X + 2*Y =< 12}, "
                + "bb_inf([X, Y], -(X + Y), V)", -10.0)]
    public void OverSeveralVariables(string goal, double expected)
    {
        var sol = Clpr().Query(goal + ".");
        Assert.True(sol.Success);
        Assert.Equal(expected, Number(sol["V"]), 9);
    }

    [Fact]
    public void NoIntegerPointMeansFailure()
    {
        // Nothing whole lives in [2.1, 2.9] ...
        Assert.False(Clpr().Query("{X >= 2.1, X =< 2.9}, bb_inf([X], X, _).").Success);
        // ... and two integers do not add up to 5.5, however wide their range.
        Assert.False(Clpr()
            .Query("{X + Y =:= 5.5, X >= 0, Y >= 0}, bb_inf([X, Y], X, _).").Success);
    }

    [Fact]
    public void AnAlreadyIntegralOptimumIsNotBranched()
    {
        // The relaxation lands on an integer, so the answer is the relaxed one.
        var e = Clpr();
        var sol = e.Query(
            "{X >= 2, X =< 8}, bb_inf([X], X, V, Vertex), Vertex = [Xv], Xv =:= 2.");
        Assert.True(sol.Success);
        Assert.Equal(2.0, Number(sol["V"]), 9);
    }

    [Fact]
    public void TheStoreIsUnchangedByAsking()
    {
        // Branching posts constraints; every branch must be undone. If the
        // x =< 1 half leaked, x could not then be 3.
        var e = Clpr();
        Assert.True(e.Query("{X >= 0.5, X =< 3.4}, bb_inf([X], X, _), {X =:= 3}.").Success);
    }
}
