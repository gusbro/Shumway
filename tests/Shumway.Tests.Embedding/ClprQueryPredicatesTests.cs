using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The predicates that ASK the store something instead of adding to it, the
/// surface SWI and SICStus share: <c>entailed/1</c>, <c>inf/2</c>,
/// <c>sup/2</c>, <c>minimize/1</c>, <c>maximize/1</c> and <c>dump/3</c>.
///
/// <para>The optimisation ones are built on the Fourier-Motzkin elimination
/// already there: state the objective as a variable, eliminate every other
/// one, and read the bounds off what is left.</para>
/// </summary>
public class ClprQueryPredicatesTests
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
    public void EntailedAsksWithoutAdding()
    {
        var e = Clpr();
        Assert.True(e.Query("{X >= 5}, entailed(X >= 3).").Success);
        Assert.False(e.Query("{X >= 5}, entailed(X >= 7).").Success);
        // What the store DERIVED counts as entailed, not just what was posted.
        Assert.True(e.Query("{A + B =:= 10}, {A =:= 6}, entailed(B =:= 4).").Success);
        // And asking must not narrow anything: X is as free afterwards as before.
        var after = e.Query("{X >= 5}, entailed(X >= 3), {X =:= 100}.");
        Assert.True(after.Success);
    }

    [Fact]
    public void EntailedTakesAConjunction()
    {
        Assert.True(Clpr().Query("{X >= 5, X =< 9}, entailed((X >= 3, X =< 20)).").Success);
        Assert.False(Clpr().Query("{X >= 5, X =< 9}, entailed((X >= 3, X =< 6)).").Success);
    }

    [Theory]
    [InlineData("{X >= 3, X =< 9}, inf(X, V)", 3.0)]
    [InlineData("{X >= 3, X =< 9}, sup(X, V)", 9.0)]
    // Through an equality: A is 10 - B, and B >= 1 bounds A above.
    [InlineData("{A >= 1, B >= 1, A + B =:= 10}, inf(A, V)", 1.0)]
    [InlineData("{A >= 1, B >= 1, A + B =:= 10}, sup(A, V)", 9.0)]
    // An objective that is an expression, not a variable.
    [InlineData("{X >= 2, X =< 4}, inf(2 * X + 1, V)", 5.0)]
    [InlineData("{X >= 2, X =< 4}, sup(2 * X + 1, V)", 9.0)]
    public void BoundsComeOutOfTheStore(string goal, double expected)
    {
        var sol = Clpr().Query(goal + ".");
        Assert.True(sol.Success);
        Assert.Equal(expected, Number(sol["V"]), 9);
    }

    [Fact]
    public void AnUnboundedObjectiveFails()
    {
        // Nothing bounds X below, so there is no infimum to report.
        Assert.False(Clpr().Query("{X >= 3}, inf(-X, _).").Success);
        Assert.False(Clpr().Query("{X + Y =:= 10}, inf(X, _).").Success);
    }

    [Fact]
    public void MinimizeAndMaximizePinTheVariable()
    {
        var lo = Clpr().Query("{X >= 2, X =< 8}, minimize(X).");
        Assert.True(lo.Success);
        Assert.Equal(2.0, Number(lo["X"]), 9);

        var hi = Clpr().Query("{X >= 2, X =< 8}, maximize(X).");
        Assert.True(hi.Success);
        Assert.Equal(8.0, Number(hi["X"]), 9);
    }

    [Fact]
    public void DumpReportsTheResidualOverNamesYouChoose()
    {
        // The store is REPORTED, not changed: the constraint comes back
        // written over the names given rather than over the variables.
        var e = Clpr();
        var sol = e.Query("{P + Q =:= 10}, dump([P, Q], ['P', 'Q'], Cs).");
        Assert.True(sol.Success);
        string rendered = sol["Cs"]!.ToString()!;
        Assert.Contains("P", rendered);
        Assert.Contains("Q", rendered);
        // P and Q themselves are untouched by the asking.
        Assert.True(e.Query("{P + Q =:= 10}, dump([P, Q], ['P', 'Q'], _), {P =:= 3}, {Q =:= 7}.").Success);
    }
}
