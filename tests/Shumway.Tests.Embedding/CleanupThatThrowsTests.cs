using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A cleanup that throws: its ball propagates from wherever the
/// cleanup ran, unless another ball is already in flight, which wins.
///
/// <para>SICStus and SWI propagate it from a cut that discards the scope; the
/// cut used to drop it, along with every other asynchronous fire. With a ball
/// in flight SWI keeps that one and SICStus the cleanup's; the goal's own
/// ball used to lose to the cleanup's while a later one won, so the two cases
/// disagreed. Every cleanup still runs: when one cut discards two scopes the
/// inner cleanup runs first and the first ball wins, as in SWI, where the
/// outer one used to be lost once the inner one's ball left the drain.</para></summary>
public sealed class CleanupThatThrowsTests
{
    private const string Program = """
        :- dynamic(log/1).
        c(cut,         (call_cleanup(member(_, [1,2]), (assertz(log(ran)), throw(oops))), !)).
        c(fail,        (call_cleanup(member(_, [1,2]), (assertz(log(ran)), throw(oops))), fail)).
        c(det_exit,    call_cleanup(true, (assertz(log(ran)), throw(oops)))).
        c(goal_fails,  call_cleanup(fail, (assertz(log(ran)), throw(oops)))).
        c(goal_throws, call_cleanup(throw(first), (assertz(log(ran)), throw(oops)))).
        c(later_throw, (call_cleanup(member(_, [1,2]), (assertz(log(ran)), throw(oops))), throw(first))).
        c(two_at_cut,  (call_cleanup(member(_, [1,2]), (assertz(log(outer)), throw(outer))),
                        call_cleanup(member(_, [1,2]), (assertz(log(inner)), throw(inner))), !)).
        c(inner_only,  (call_cleanup(member(_, [1,2]), assertz(log(outer))),
                        call_cleanup(member(_, [1,2]), (assertz(log(inner)), throw(inner))), !)).

        run(N, R-L) :-
            retractall(log(_)), c(N, G),
            catch((G -> R = succeeded ; R = failed), E, R = caught(E)),
            findall(X, log(X), L).

        % A failure-driven loop over the cases: the choice point of c/2 is
        % older than every catch, and must survive a ball out of a drain.
        all(Rs) :-
            retractall(result(_, _)),
            ( c(N, _), run(N, R), assertz(result(N, R)), fail ; true ),
            findall(N-R, result(N, R), Rs).
        :- dynamic(result/2).

        % A cut inside a cleanup runs what the cleanup's own scope queued
        % before the goal after it.
        nested(V) :-
            call_cleanup(member(_, [1,2]),
                ( b_setval(k, before),
                  call_cleanup(member(_, [1,2]), b_setval(k, cleaned)), !,
                  b_getval(k, V0), assertz(log(V0)) )),
            !, log(V).
        nested_run(V) :- retractall(log(_)), nested(V).
        """;

    private static PrologEngine Engine(bool compiled)
    {
        var e = new PrologEngine();
        e.ConsultString(Program);
        if (compiled) e.IlPromotion.Threshold = 1;
        return e;
    }

    [Theory]
    [InlineData("cut",         "caught(oops)-[ran]")]
    [InlineData("fail",        "caught(oops)-[ran]")]
    [InlineData("det_exit",    "caught(oops)-[ran]")]
    [InlineData("goal_fails",  "caught(oops)-[ran]")]
    [InlineData("goal_throws", "caught(first)-[ran]")]
    [InlineData("later_throw", "caught(first)-[ran]")]
    [InlineData("two_at_cut",  "caught(inner)-[inner,outer]")]
    [InlineData("inner_only",  "caught(inner)-[inner,outer]")]
    public void TheBallAndTheCleanupsThatRan(string fire, string expected)
    {
        foreach (bool compiled in new[] { false, true })
        {
            var e = Engine(compiled);
            for (int i = 0; i < 3; i++)
            {
                var s = e.Query($"run({fire}, R), with_output_to(atom(A), print(R)).");
                Assert.True(s.Success, $"{fire} (compiled: {compiled}, run {i})");
                Assert.Equal(expected, s["A"]!.ToString());
            }
        }
    }

    [Fact]
    public void ALoopOverTheFiresKeepsItsChoicePoints()
    {
        foreach (bool compiled in new[] { false, true })
        {
            var s = Engine(compiled).Query("all(Rs), length(Rs, N).");
            Assert.True(s.Success, $"compiled: {compiled}");
            Assert.Equal("8", s["N"]!.ToString());
        }
    }

    [Fact]
    public void ACutInsideACleanupRunsTheCleanupsItQueued()
    {
        foreach (bool compiled in new[] { false, true })
        {
            var e = Engine(compiled);
            for (int i = 0; i < 3; i++)
            {
                var s = e.Query("nested_run(V).");
                Assert.True(s.Success, $"compiled: {compiled}, run {i}");
                Assert.Equal("cleaned", s["V"]!.ToString());
            }
        }
    }
}
