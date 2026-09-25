using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Scryer implements <c>length/2</c> in Prolog, in its own
/// <c>lists.pl</c>, over two primitives its Rust runtime provides. Without
/// them, <c>length(L, 3)</c> with L unbound raises existence_error the
/// moment a program imports their <c>library(lists)</c> -- and every clpz
/// program does, because clpz.pl imports it itself.
///
/// <para>They live in the prelude beside the other Scryer internals
/// (<c>'$skip_max_list'/4</c>, <c>'$get_attr_list'/2</c>,
/// <c>'$absent_attr'/3</c>) rather than in the engine: a compat name is the
/// shim's business, and these are spelled the way Scryer spells them.</para>
/// </summary>
public sealed class ScryerListPrimitivesTests
{
    private static PrologEngine Engine() => new();

    /// <summary>A plain unbound variable carries no attributes.</summary>
    [Fact]
    public void APlainVariableIsUnattributed()
        => Assert.True(Engine().Query("'$unattributed_var'(_).").Success);

    /// <summary>A non-variable fails, which is Scryer's first check.</summary>
    [Theory]
    [InlineData("a")]
    [InlineData("f(x)")]
    [InlineData("[1,2]")]
    [InlineData("42")]
    public void ANonVariableIsNotUnattributed(string term)
        => Assert.False(Engine().Query($"'$unattributed_var'({term}).").Success);

    /// <summary>An ATTRIBUTED variable fails too: that is the whole point of
    /// the name, and the reason it cannot just be var/1. Scryer fails when
    /// the attribute list is a cons.</summary>
    [Fact]
    public void AnAttributedVariableIsNotUnattributed()
    {
        var e = Engine();
        Assert.False(e.Query("put_attr(X, m, hello), '$unattributed_var'(X).").Success);
        // ...and it goes back to succeeding once the attribute is removed,
        // so the test is about the ATTRIBUTE and not about the variable.
        Assert.True(e.Query(
            "put_attr(X, m, hello), del_attr(X, m), '$unattributed_var'(X).").Success);
    }

    /// <summary>A bound variable follows its binding, like every other
    /// var check.</summary>
    [Fact]
    public void ABoundVariableFollowsItsBinding()
    {
        Assert.False(Engine().Query("X = a, '$unattributed_var'(X).").Success);
        Assert.True(Engine().Query("X = Y, '$unattributed_var'(X).").Success);
    }

    /// <summary>The companion: a fresh list of N variables.</summary>
    [Fact]
    public void DetLengthRundownBuildsAFreshList()
    {
        var e = Engine();
        Assert.True(e.Query("'$det_length_rundown'(L, 3), L = [_,_,_].").Success);
        Assert.True(e.Query("'$det_length_rundown'(L, 0), L == [].").Success);
        // Fresh, not shared: binding one must not bind the others.
        Assert.True(e.Query(
            "'$det_length_rundown'(L, 3), L = [A,B,C], A = 1, var(B), var(C).").Success);
    }

    /// <summary>THE CASE THIS EXISTS FOR, end to end: Scryer's own length/2
    /// over the two primitives. The clause is theirs, transcribed, so the
    /// test fails if either primitive drifts from what it expects.</summary>
    [Fact]
    public void ScryerLengthRundownWorksOverBothPrimitives()
    {
        var e = Engine();
        // lists.pl's length_rundown/2, verbatim in shape.
        e.ConsultString("""
            length_rundown(Xs, 0) :- !, Xs = [].
            length_rundown(Vs, N) :-
                '$unattributed_var'(Vs), !,
                '$det_length_rundown'(Vs, N).
            length_rundown([_|Xs], N) :- N1 is N - 1, length_rundown(Xs, N1).
            """);
        Assert.True(e.Query("length_rundown(L, 4), L = [_,_,_,_].").Success,
            "Scryer's length/2 rundown path is still broken");
        // The partial-list path still goes the other way.
        Assert.True(e.Query("length_rundown([a|T], 3), T = [_,_].").Success);
    }
}
