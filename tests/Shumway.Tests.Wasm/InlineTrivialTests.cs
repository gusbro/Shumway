using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary><c>fail/0</c> and <c>true/0</c>, whose whole implementation is a
/// verdict, answered inside the module.
///
/// <para>clp(Z) left the module 72 times in one goal to be told no by a
/// predicate whose body is <c>=> false</c>, one host round trip each. A
/// failure driven loop pays it on every round.</para></summary>
public sealed class InlineTrivialTests(ITestOutputHelper o)
{
    private const string Corpus = """
        f(X) :- member(X, [a, b, c]), X == b, fail.
        f(done).
        % fail after a CUT: the cut discards q/1's alternatives, so the
        % failure leaves the predicate instead of re-entering them.
        cut_then_fail(R) :- ( cut_fails -> R = yes ; R = no ).
        cut_fails :- q(_), !, fail.
        q(1). q(2).
        % In the middle of a body, and in the last position.
        mid(R)  :- ( true, R = yes, true ; R = no ).
        tail(R) :- ( r(R), fail ; R = after ).
        r(1). r(2).
        % A failure-driven loop, which is where the round trips pile up.
        loop(N) :- between(1, 40, _), fail ; N = 40.
        """;

    [Theory]
    [InlineData("f(X), X == done.")]
    [InlineData("cut_then_fail(R), R == no.")]
    [InlineData("mid(R), R == yes.")]
    [InlineData("tail(R), R == after.")]
    [InlineData("loop(N), N == 40.")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>And it really is inline: a failure-driven loop leaves no
    /// builtin request behind for the failing itself. The anti-vacuity guard
    /// is in the same assertion -- between/3 keeps exiting, so a zero here
    /// cannot be a program that never reached the tier.</summary>
    [DiagFact]
    public void FailingDoesNotLeaveTheModule()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("loop(N), N == 40.").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query("loop(N), N == 40.").Success);

        long fail = 0, between = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
        {
            if (name == "fail" && arity == 0) fail = hits;
            if (name == "between" && arity == 3) between = hits;
        }
        o.WriteLine($"fail/0 exits={fail} between/3 exits={between}");
        Assert.True(between > 0, "the loop never ran on the tier");
        Assert.Equal(0L, fail);
    }
}
