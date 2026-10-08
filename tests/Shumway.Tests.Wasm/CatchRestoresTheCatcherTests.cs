using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>ISO 7.8.9 on the wasm tier: a ball meets the catcher as it was
/// when the catch began, so a goal that bound a catcher variable and then
/// threw is caught. A module throws out to the host's catch resolution, and
/// the bindings it undoes for the match are the ones the module trailed.
/// </summary>
public sealed class CatchRestoresTheCatcherTests
{
    private const string Corpus = """
        mem(X, [X|_]).
        mem(X, [_|T]) :- mem(X, T).
        t1(R) :- catch((R = true, throw(oops)), R, true).
        t4(R) :- catch(g4(R), R, true).
        g4(R) :- R = true, throw(oops).
        t6(R) :- catch((R = f(_), throw(oops)), R, true).
        t8(L) :- catch((mem(X, [1,2,3]), X >= 2, R = true, throw(X)), R, true), L = R.
        t9(R) :- catch((R = f(A), A = 1, throw(f(2))), R, true).
        t10(R) :- R = other, catch(throw(oops), R, uncaught).
        t12(R) :- catch(catch((R = a, throw(b)), a, true), R, true).
        """;

    [Theory]
    [InlineData("t1(R), R == oops")]
    [InlineData("t4(R), R == oops")]
    [InlineData("t6(R), R == oops")]
    [InlineData("t8(L), L == 2")]
    [InlineData("t9(R), R == f(2)")]
    [InlineData("catch(t10(_), B, true), B == oops")]
    [InlineData("t12(R), R == b")]
    public void TheAnswerIsTheEnginesEitherWay(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query($"{goal}.").Success, $"Tier-0: {goal}");
        var (tier, _, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        // Twice: the second call runs the module the first one promoted.
        Assert.True(tier.Query($"{goal}.").Success, $"tier, first call: {goal}");
        Assert.True(tier.Query($"{goal}.").Success, $"tier: {goal}");
    }
}
