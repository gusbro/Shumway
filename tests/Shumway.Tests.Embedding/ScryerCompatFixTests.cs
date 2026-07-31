using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Small engine fixes surfaced by the Scryer library triage
/// (docs/library-triage-scryer.md): the bar as an atom, DCG pushback grouping,
/// and the <c>$is_partial_string/1</c> fast test.</summary>
public sealed class ScryerCompatFixTests
{
    [Fact]
    public void BarAsAtom_Parses()
    {
        // ISO: `|` is an atom. `(|)` is that atom — Scryer's builtins.pl op/3
        // permission-error term relies on it.
        var e = new PrologEngine();
        Assert.True(e.Query("X = (|), X == '|'.").Success);
        Assert.True(e.Query("functor((|), N, 0), N == '|'.").Success);
    }

    [Fact]
    public void DcgPushback_HeadGroupsUnderRealHead()
    {
        // A pushback (semicontext) DCG rule + a plain rule of the SAME
        // nonterminal must be contiguous — they group under the real head, not
        // `,/4`. `peek(X), [X] --> [X]` is a lookahead: consume X, push it back.
        var e = new PrologEngine();
        e.ConsultString(
            "peek(X), [X] --> [X].\n"
            + "peek_or_end(X) --> peek(X).\n"
            + "peek_or_end(end) --> [].\n"
            + "t(X, Rest) :- phrase(peek(X), [a, b, c], Rest).\n");
        // Lookahead: X = a, and the input is UNCONSUMED (Rest still [a,b,c]).
        Assert.True(e.Query("t(a, [a, b, c]).").Success);
    }

    [Fact]
    public void IsPartialString_RejectsNonStrings()
    {
        // The fast test rejects atoms, numbers and vars (the positive PSTR case
        // is exercised at load level by crypto/ffi/uuid in the opt-in project).
        var e = new PrologEngine();
        Assert.False(e.Query("'$is_partial_string'(foo).").Success);
        Assert.False(e.Query("'$is_partial_string'(42).").Success);
        Assert.False(e.Query("'$is_partial_string'(_).").Success);
    }
}
