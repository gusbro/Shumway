using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>when/2 watches only the variables whose binding can make its
/// condition true: the variable of nonvar/1, one variable of ground/1, the
/// first conjunct that does not hold, every branch of a disjunction (as
/// SICStus does). Watching every variable the condition mentions left a dead
/// record per re-attachment and walked the whole condition each time, a long
/// list that an already-satisfied part held included: a same_length/2 built
/// on when((nonvar(X), nonvar(Y)), ...) went quadratic.</summary>
public sealed class WhenWatchesWhatDecidesTests(ITestOutputHelper o)
{
    private static PrologEngine Co()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- use_module(library(coroutining)).
            :- dynamic(hits/1).
            hit :- ( retract(hits(N)) -> true ; N = 0 ), N1 is N + 1, assertz(hits(N1)).
            records(V, C) :-
                (   var(V), get_attr(V, coroutining, frozen(G)) -> count_conj(G, C)
                ;   C = 0
                ).
            count_conj(G, 0) :- var(G), !.
            count_conj((A, B), N) :- !, count_conj(A, NA), count_conj(B, NB), N is NA + NB.
            count_conj(_, 1).
            sl(X, Y) :- when((nonvar(X), nonvar(Y)), sl1(X, Y)).
            sl1([], []).
            sl1([_|X], [_|Y]) :- sl(X, Y).
            open_tail(L, L) :- var(L), !.
            open_tail([_|T], E) :- open_tail(T, E).
            grow(0, _, _) :- !.
            grow(N, X, Y) :- open_tail(X, XT), XT = [_|_], N1 is N - 1, grow(N1, X, Y).
            sl_test(N, Len) :- sl(Y, Z), numlist(1, 100, X), findall(I, member(I, X), Y, _),
                numlist(1, N, Z), length(Y, Len).
            """);
        return e;
    }

    [Theory]
    // The unwatched variable bound first: nothing yet, then once.
    [InlineData(@"when((nonvar(X), nonvar(Y)), hit), Y = 1, \+ hits(_), X = 2, hits(1).")]
    [InlineData(@"when((nonvar(X), nonvar(Y)), hit), X = 1, \+ hits(_), Y = 2, hits(1).")]
    [InlineData(@"when(ground(f(V, W)), hit), W = 1, \+ hits(_), V = 2, hits(1).")]
    [InlineData(@"when(ground(f(V, W)), hit), V = 1, \+ hits(_), W = 2, hits(1).")]
    [InlineData("when((nonvar(X) ; nonvar(Y)), hit), Y = 1, X = 2, hits(1).")]
    [InlineData("when(((nonvar(X), nonvar(Y)) ; ground(Z)), hit), Z = z, hits(1), X = 1, Y = 2, hits(1).")]
    [InlineData(@"when(?=(X, Y), hit), X = f(A), Y = f(B), \+ hits(_), A = B, hits(1).")]
    public void FiresOnceWhenTheConditionHolds(string query)
        => Assert.True(Co().Query(query).Success, query);

    [Fact]
    public void AConjunctionWatchesOneVariable()
        => Assert.True(Co().Query(
            "when((nonvar(X), nonvar(Y)), true), records(X, CX), records(Y, CY), "
            + "1 is CX + CY.").Success);

    [Fact]
    public void AnOpenTailGathersNoDeadRecords()
    {
        // X grows a cell at a time; Y's tail is the open end the constraint
        // re-suspends on. Watching the whole condition left one record per
        // step there.
        var sol = Co().Query("sl(X, Y), grow(50, X, Y), open_tail(Y, YT), records(YT, C).");
        Assert.True(sol.Success);
        o.WriteLine($"records on the open tail after 50 steps: {sol["C"]}");
        Assert.True(long.Parse(sol["C"]!.ToString()!) <= 2);
    }

    [Fact]
    public void ALongSatisfiedPartIsNotWalkedAtEachStep()
    {
        // Each step re-suspends same_length(T, Z) with Z, the rest of a
        // 20,000-element list, already bound: walking it at every step was
        // minutes in a Debug build; linear is under a second.
        var e = Co();
        bool? ok = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { ok = e.Query("sl_test(20000, Len), Len == 20000.").Success; }
            catch (Exception ex) { error = ex; }
        }, 64 * 1024 * 1024) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "sl_test(20000) did not finish within 60 s");
        Assert.Null(error);
        Assert.True(ok);
    }
}
