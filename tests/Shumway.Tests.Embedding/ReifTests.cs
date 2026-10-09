using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>library(reif) and the reified comparisons of clpfd. The answers,
/// their order and their constraints are what Scryer's library(reif) and
/// clpz give; where Scryer leaves a choice point although the arguments
/// decide the answer, these do not.</summary>
public sealed class ReifTests
{
    private const string Program = """
        :- use_module(library(reif)).
        :- use_module(library(clpfd)).

        % G succeeded and left no choice point.
        det(G) :- setup_call_cleanup(true, G, D = true), D == true.
        % Every answer of G, and whether the last one left no choice point.
        answers(Vs, G, As, Last) :-
            findall(Vs-D, ( setup_call_cleanup(true, G, D0 = true),
                            ( D0 == true -> D = det ; D = nondet ) ), Ps),
            pairs_keys_values(Ps, As, Ds),
            last(Ds, Last).

        bad(maybe).
        unbound(_).
        """;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(Program);
        return e;
    }

    private static void Holds(string goal)
        => Assert.True(Engine().Query(goal + ".").Success, goal);

    [Theory]
    [InlineData("=(a, a, T), T == true")]
    [InlineData("=(a, b, T), T == false")]
    [InlineData("=(f(X), f(a), true), X == a")]
    [InlineData("=(f(_, b), f(a, c), T), T == false")]
    [InlineData("=(X, X, T), T == true")]
    [InlineData("dif(a, a, T), T == false")]
    [InlineData(@"dif(X, a, true), \+ X = a, X = b")]
    [InlineData("dif(X, a, false), X == a")]
    // A decided condition, or a truth value given, leaves no choice point.
    [InlineData("det(=(a, b, _))")]
    [InlineData("det(=(X, a, true)), X == a")]
    [InlineData(@"det(=(X, a, false)), \+ X = a")]
    [InlineData("det(dif(X, a, false)), X == a")]
    [InlineData("det(if_(a = a, R = yes, R = no)), R == yes")]
    [InlineData("det(memberd_t(b, [a,b,c], T)), T == true")]
    [InlineData("det(tfilter(=(a), [a,b], L)), L == [a]")]
    public void Decides(string goal) => Holds(goal);

    [Theory]
    // Undecided: true first with the unification, then false with dif/2,
    // and the last answer leaves no choice point.
    [InlineData("answers(X-T, =(X, a, T), As, det), As = [a-true, V-false], var(V)")]
    [InlineData("answers(X-T, dif(X, a, T), As, det), As = [a-false, V-true], var(V)")]
    [InlineData("answers(R, if_(=(_, a), R = yes, R = no), As, det), As == [yes, no]")]
    [InlineData("answers(X-Y-T, ','(=(X, a), =(Y, b), T), As, det), As = [a-b-true, a-_-false, _-_-false]")]
    [InlineData("answers(X-Y-T, ;(=(X, a), =(Y, b), T), As, det), As = [a-_-true, _-b-true, _-_-false]")]
    [InlineData("answers(R, if_((X = a ; X = b), R = inside, R = outside), As, det), As == [inside, inside, outside]")]
    [InlineData("answers(X-T, memberd_t(X, [a,b], T), As, det), As = [a-true, b-true, V-false], var(V)")]
    [InlineData("answers(X-Y, tfilter(=(a), [X,Y], _), As, det), length(As, 4)")]
    [InlineData("answers(Ts-Fs, tpartition(=(a), [a,b,X], Ts, Fs), As, det), As = [[a,a]-[b], [a]-[b,_]]")]
    public void AnswersInOrder(string goal) => Holds(goal);

    [Theory]
    // Each answer carries its constraint.
    [InlineData(@"=(X, a, T), T == false, \+ X = a")]
    [InlineData(@"memberd_t(X, [a,b], false), \+ X = a, \+ X = b, X = c")]
    [InlineData(@"tfilter(=(a), [X], []), \+ X = a")]
    [InlineData(@"tmember_t(=(a), [X, a], true), ( X == a ; \+ X = a )")]
    [InlineData("cond_t(=(X, a), Y = 1, T), T == true, X == a, Y == 1")]
    [InlineData("cond_t(=(a, b), Y = 1, T), T == false, var(Y)")]
    [InlineData("tmember(=(a), [b, X]), X == a")]
    [InlineData("tmember_t(=(a), [b, c], T), T == false")]
    public void Constrains(string goal) => Holds(goal);

    [Theory]
    [InlineData("if_(bad, a, b)", "type_error(boolean, maybe)")]
    [InlineData("if_(unbound, a, b)", "instantiation_error")]
    [InlineData("if_(_, a, b)", "instantiation_error")]
    public void Raises(string goal, string error)
        => Holds($"catch(({goal}, fail), error({error}, _), true)");

    [Theory]
    // A truth value that is neither true nor false is no answer.
    [InlineData("=(a, a, maybe)")]
    [InlineData("dif(X, a, maybe)")]
    [InlineData("tfilter(=(a), foo, _)")]
    [InlineData("X in 0..2, clpfd_t(X #= 1, maybe)")]
    public void Fails(string goal) => Assert.False(Engine().Query(goal + ".").Success, goal);

    [Fact]
    public void TheCutOfABranchIsLocalToIt()
        => Holds("findall(R, if_(=(_, a), (R = yes, !), R = no), Rs), Rs == [yes, no]");

    [Fact]
    public void AConditionIsCalledInTheCallersModule()
    {
        var e = Engine();
        e.ConsultString("""
            :- module(reif_mod, [run/2]).
            :- use_module(library(reif)).
            is_a(X, T) :- =(X, a, T).
            run(L, R) :- tfilter(is_a, [a,b,a], L), if_(is_a(b), R = yes, R = no).
            """);
        Assert.True(e.Query("run(L, R), L == [a,a], R == no.").Success);
    }

    [Theory]
    // clpfd's comparisons as reified conditions: false first, then true.
    [InlineData("#=(3, 3, T), T == true")]
    [InlineData("#>(3, 2, T), T == true")]
    [InlineData(@"#\=(3, 2, T), T == true")]
    [InlineData("#=<(3, 2, T), T == false")]
    [InlineData("X in 0..2, answers(T, #=(X, 1, T), As, det), As == [false, true]")]
    [InlineData(@"X in 0..2, #=(X, 1, false), fd_dom(X, D), D == (0 \/ 2)")]
    [InlineData("X in 0..5, #>=(X, 3, true), fd_inf(X, I), I == 3")]
    [InlineData("X in 0..5, answers(R, if_(X #< 3, R = lt, R = ge), As, det), As == [ge, lt]")]
    [InlineData("tfilter(#<(2), [1,2,3,4], L), L == [3,4]")]
    [InlineData("det(if_(2 #< 3, R = lt, R = ge)), R == lt")]
    // A connective decided by the truth value fixes both sides.
    [InlineData(@"[X,Y] ins 0..1, clpfd_t(X #= 1 #/\ Y #= 0, true), X == 1, Y == 0")]
    [InlineData(@"[X,Y] ins 0..1, clpfd_t(X #= 1 #\/ Y #= 0, false), X == 0, Y == 1")]
    public void ClpfdReifiedComparisons(string goal) => Holds(goal);

    [Fact]
    public void TreallasClpzReifierIsClpfdT()
    {
        var e = new PrologEngine();
        e.SetLibraryDialect("trealla");
        e.ConsultString(":- use_module(library(clpz)).");
        Assert.True(e.Query("X in 0..2, clpz_t(X #= 1, true), X == 1.").Success);
    }
}
