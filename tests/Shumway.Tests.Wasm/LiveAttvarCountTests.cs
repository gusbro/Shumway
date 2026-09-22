using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>How many attributed variables the engine still holds when a
/// goal is done.
///
/// <para>This is the seed of the clp(Z) runaway, reached by diffing the two
/// tiers' builtin sequences: the first 2,622 calls agree exactly, and then
/// the tier makes one <c>put_attr/3</c> the interpreter does not. In
/// context both are inside <c>copy_term/3</c>'s preparation, copying the
/// attributes of the live attributed variables -- Tier 0 copies eight, the
/// tier copies nine. One variable too many, and the projection that follows
/// has a propagator too many to render.</para>
///
/// <para><c>'$live_attvars'/1</c> asks exactly that question: it walks the
/// attribute table and keeps the addresses whose cell is STILL an
/// attributed variable. A tier that leaves one behind -- a slot
/// backtracking freed and something reused, a record no sweep reached --
/// reports one more here and nowhere else, because the answers stay
/// right.</para></summary>
public sealed class LiveAttvarCountTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(clpfd)).
        live(N) :- '$live_attvars'(L), length(L, N).
        % Constrained, then abandoned by backtracking: nothing should be
        % left holding an attribute afterwards.
        undone(N) :- \+ \+ (X in 1..9, Y in 1..9, X #< Y, label([X,Y])), live(N).
        % The same with a solved, committed goal: the labelled variables are
        % bound, so their attributes are gone too.
        solved(N) :- Vs = [A,B,C], Vs ins 1..3, all_different(Vs),
                     A #< B, label(Vs), live(N).
        % A goal that fails outright.
        failed(N) :- \+ (X in 1..2, X #> 5), live(N).
        % Still standing on purpose: an anti-vacuity case, so a probe that
        % could never see an attributed variable would fail here.
        standing(N) :- _X in 1..9, live(N).
        % The driver the browser stage actually uses: a failure-driven
        % loop around a double negation. Backtracking out of it must
        % leave no attributed cell standing.
        loop(N) :-
            ( between(1, 1, _),
              \+ \+ (Vs = [A,B,C], Vs ins 1..3, all_different(Vs),
                      A #< B, label(Vs)),
              fail
            ; true
            ),
            live(N).
        % The same with the constrained variables reachable from OUTSIDE
        % the negation, which is how clp(Z)'s stage is written.
        loopout(N) :-
            Vs = [_,_,_],
            ( between(1, 1, _),
              \+ \+ (Vs ins 1..3, all_different(Vs), label(Vs)),
              fail
            ; true
            ),
            live(N).
        """;

    [Theory]
    [InlineData("undone")]
    [InlineData("solved")]
    [InlineData("failed")]
    [InlineData("standing")]
    [InlineData("loop")]
    [InlineData("loopout")]
    public void BothTiersHoldTheSameAttributedVariables(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        string p0 = Count(plain, goal);

        var (tiered, _) = TieredEngine.Build(Corpus);
        string t = Count(tiered, goal);
        o.WriteLine($"{goal}: tier0={p0} tier={t}");
        Assert.Equal(p0, t);
    }

    /// <summary>Anti-vacuity: the probe must be able to see one at all.
    /// </summary>
    [Fact]
    public void TheProbeSeesAStandingAttributedVariable()
    {
        var e = new PrologEngine();
        e.ConsultString(Corpus);
        Assert.False(Count(e, "standing") is "0" or "failed",
            "the probe never sees an attributed variable, so it proves nothing");
    }

    private static string Count(PrologEngine e, string goal)
    {
        try
        {
            var r = e.Query($"{goal}(N).");
            if (!r.Success) return "failed";
            foreach (var b in r.Bindings) if (b.Key == "N") return b.Value.ToString()!;
            return "?";
        }
        catch (System.Exception ex) { return ex.Message; }
    }
}
