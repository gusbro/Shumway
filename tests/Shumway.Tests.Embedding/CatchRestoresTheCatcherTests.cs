using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ISO 7.8.9: a thrown ball meets the catcher as it was when the
/// catch began. What the guarded goal bound since, the catcher's own
/// variables included, is undone first.
///
/// <para>Shumway tested the match before the rollback, on the catcher as
/// the goal left it, and a goal that had bound a catcher variable before
/// throwing was never caught: <c>catch((R = true, throw(oops)), R, true)</c>
/// raised oops. The twelve goals here answer the same in SICStus, Scryer and
/// GNU Prolog.</para></summary>
public sealed class CatchRestoresTheCatcherTests
{
    private const string Program = """
        mem(X, [X|_]).
        mem(X, [_|T]) :- mem(X, T).
        t1(R) :- catch((R = true, throw(oops)), R, true).
        t2(R) :- catch((R = true, throw(oops)), R, true), R == oops.
        t3(R) :- G = (R = true, throw(oops)), catch(G, R, true).
        t4(R) :- catch(g4(R), R, true).
        g4(R) :- R = true, throw(oops).
        t5(R, S) :- catch((S = bound, throw(oops)), R, true).
        t6(R) :- catch((R = f(_), throw(oops)), R, true).
        t7(R) :- catch((R = true, atom_length(_, _)), error(R, _), true).
        t8(L) :- catch((mem(X, [1,2,3]), X >= 2, R = true, throw(X)), R, true), L = R.
        t9(R) :- catch((R = f(A), A = 1, throw(f(2))), R, true).
        t10(R) :- R = other, catch(throw(oops), R, true).
        t11(R, X) :- catch((R = 1, throw(ball(R))), ball(X), true).
        t12(R) :- catch(catch((R = a, throw(b)), a, true), R, true).
        """;

    [Theory]
    [InlineData("t1(R)", "yes", "R", "oops")]
    [InlineData("t2(R)", "yes", "R", "oops")]
    [InlineData("t3(R)", "yes", "R", "oops")]
    [InlineData("t4(R)", "yes", "R", "oops")]
    [InlineData("t5(R, S)", "yes", "R", "oops")]
    [InlineData("t6(R)", "yes", "R", "oops")]
    [InlineData("t7(R)", "yes", "R", "instantiation_error")]
    [InlineData("t8(L)", "yes", "L", "2")]
    [InlineData("t9(R)", "yes", "R", "f(2)")]
    [InlineData("t10(R)", "uncaught(oops)", "R", "other")]
    [InlineData("t11(R, X)", "yes", "X", "1")]
    [InlineData("t12(R)", "yes", "R", "b")]
    public void TheCatcherIsTheOneTheCatchBeganWith(string goal, string outcome, string var, string value)
    {
        foreach (bool compiled in new[] { false, true })
        {
            var e = new PrologEngine();
            e.ConsultString(Program);
            e.IlPromotion.Threshold = compiled ? 1 : 0;
            for (int round = 0; round < (compiled ? 2 : 1); round++)
            {
                var s = e.Query($"catch(({goal}, O__ = yes), B__, O__ = uncaught(B__)).");
                Assert.True(s.Success, goal);
                string text(string v) => AstTermRenderer.Render(s[v]!, 400, e.Operators).Replace(" ", "");
                Assert.Equal(outcome, text("O__"));
                if (outcome == "yes") Assert.Equal(value, text(var));
            }
        }
    }

    [Fact]
    public void WhatTheGoalBoundElsewhere_IsUndoneToo()
    {
        var e = new PrologEngine();
        e.ConsultString(Program);
        var s = e.Query("t5(R, S), var(S).");
        Assert.True(s.Success);
        // The ball is a copy taken when thrown, so t11 keeps X = 1 with R free again.
        Assert.True(e.Query("t11(R, X), var(R), X == 1.").Success);
    }

    // A goal woken by a binding runs at the next call, after what follows
    // the binding inline: here `R = true` has run when the goal throws.
    [Fact]
    public void AThrowFromAWokenGoal_IsCaughtByACatcherTheGoalHadBound()
    {
        var e = new PrologEngine();
        e.UseCoroutining();
        var s = e.Query("catch((freeze(X, throw(oops)), X = 1, R = true), R, true).");
        Assert.True(s.Success);
        Assert.Equal("oops", AstTermRenderer.Render(s["R"]!, 100, e.Operators));

        var fd = new PrologEngine();
        fd.UseClpfd();
        s = fd.Query("catch((X #> 0, X = 100000000000000000000, R = true), error(R, _), true).");
        Assert.True(s.Success);
        Assert.Equal("representation_error(max_clpfd_integer)",
            AstTermRenderer.Render(s["R"]!, 100, fd.Operators));
    }

    // The frames a throw passes are many and bind nothing of the catcher:
    // the match is tested once per frame, on the machine as it is.
    [Fact]
    public void AThrowThroughManyFrames_StillPasses()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            deep(0, G) :- !, G.
            deep(N, G) :- M is N - 1, catch(deep(M, G), -, true).
            """);
        var s = e.Query("catch(deep(20000, throw(ball)), B, true).");
        Assert.True(s.Success);
        Assert.Equal("ball", AstTermRenderer.Render(s["B"]!, 100, e.Operators));
    }
}
