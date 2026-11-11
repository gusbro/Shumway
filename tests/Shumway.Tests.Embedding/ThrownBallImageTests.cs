using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A caught throw plants its ball from the ADR-063 image throw/1 took
/// of it, and falls back to the term for a ball an image cannot carry. Either
/// way the catcher meets a copy of the ball as it was thrown (ISO 7.8.9 and
/// 7.8.10), whichever path delivered it: the interpreter's jump to a frame
/// opened in the same dispatch, a throw that leaves a nested driver, and
/// compiled code.</summary>
public sealed class ThrownBallImageTests
{
    private const string Program = """
        thrower(direct, B) :- throw(B).
        thrower(nested, B) :- findall(x, throw(B), _).

        shares(M) :-
            catch(thrower(M, f(X, X, _)), f(A, B, C), true),
            A == B, A \== C, A \== X, var(X).
        undoes(M) :-
            catch((X = g(1), thrower(M, b(X))), b(Y), true),
            Y == g(1), var(X).
        drops_attributes(M) :-
            put_attr(V, m, 1),
            catch(thrower(M, b(V)), b(W), true),
            W \== V, \+ get_attr(W, m, _), get_attr(V, m, 1).
        keeps_floats_and_text(M) :-
            catch(thrower(M, b(1.5, "ab", 'x y', [])), b(F, S, A, N), true),
            F == 1.5, S == "ab", A == 'x y', N == [].
        keeps_a_cycle(M) :-
            X = f(X),
            catch(thrower(M, b(X)), b(Y), true),
            \+ acyclic_term(Y), Y = f(Z), Z = f(_).
        keeps_big_numbers(M) :-
            catch(thrower(M, b(123456789012345678901234567890)), b(N), true),
            N =:= 123456789012345678901234567890.
        keeps_a_list_ball(M) :-
            catch(thrower(M, [a, b|T]), [P, Q|U], true),
            P == a, Q == b, var(U), U \== T.
        % Deeper than a recursive writer goes: a caught ball is never written,
        % not even into the message of the exception that carries it.
        keeps_a_long_list(M) :-
            numlist(1, 200000, L),
            catch(thrower(M, b(L)), b(K), true),
            length(K, Len), Len == 200000, K = [1|_].
        keeps_a_resource_error(M) :-
            catch(thrower(M, error(resource_error(memory), ctx)),
                  error(resource_error(R), C), true),
            R == memory, C == ctx.
        passes_a_catcher_that_does_not_match(M) :-
            catch(catch(thrower(M, outer(1)), inner(_), fail), outer(X), true),
            X == 1.
        """;

    [Theory]
    [InlineData("shares")]
    [InlineData("undoes")]
    [InlineData("drops_attributes")]
    [InlineData("keeps_floats_and_text")]
    [InlineData("keeps_a_cycle")]
    [InlineData("keeps_big_numbers")]
    [InlineData("keeps_a_list_ball")]
    [InlineData("keeps_a_long_list")]
    [InlineData("keeps_a_resource_error")]
    [InlineData("passes_a_catcher_that_does_not_match")]
    public void TheCatcherMeetsACopyOfTheBallAsThrown(string check)
    {
        foreach (bool compiled in new[] { false, true })
        {
            var e = new PrologEngine();
            e.ConsultString(Program);
            e.IlPromotion.Threshold = compiled ? 1 : 0;
            for (int round = 0; round < (compiled ? 2 : 1); round++)
            {
                foreach (string mode in new[] { "direct", "nested" })
                    Assert.True(e.Query($"{check}({mode}).").Success,
                        $"{check}({mode}), {(compiled ? "compiled" : "interpreted")}, round {round}");
                if (compiled)
                    Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
            }
        }
    }

    private const string Loop = """
        ball_loop(0) :- !.
        ball_loop(N) :-
            Vs = [_, _, _, _, _, _, _, _, _], tag(Vs, 1, Gs),
            catch(throw(b(Vs, Gs)), b(_, _), true),
            N1 is N - 1, ball_loop(N1).
        tag([], _, []).
        tag([V|Vs], I, [g(V, I)|Gs]) :- I1 is I + 1, tag(Vs, I1, Gs).
        """;

    /// <summary>What a caught throw allocates, on a ball of nine variables
    /// and nine structures: the image's cells, not a term of it planted once
    /// per catcher tried. The difference of two runs cancels what a query
    /// costs whatever it does, and leaves 2,000 throws.</summary>
    [Fact]
    public void ACaughtThrowDoesNotBuildItsBallAsATerm()
    {
        var e = new PrologEngine();
        e.ConsultString(Loop);
        e.IlPromotion.Threshold = 0;
        Assert.True(e.Query("ball_loop(10).").Success);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(e.Query("ball_loop(1000).").Success);
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(e.Query("ball_loop(3000).").Success);
        long a2 = GC.GetAllocatedBytesForCurrentThread();
        long perThrow = ((a2 - a1) - (a1 - a0)) / 2000;
        // The image is some 70 cells here; the term path allocated several
        // times the bound.
        Assert.True(perThrow < 2000, $"{perThrow} bytes per caught throw");
    }
}
