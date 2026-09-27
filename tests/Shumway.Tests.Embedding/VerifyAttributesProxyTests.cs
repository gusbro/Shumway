using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A <c>verify_attributes/3</c> hook receives a proxy for the
/// variable the unifier already bound. Once the hook accepts, the proxy is
/// bound to the same term: it must not outlive the hook as a live
/// attributed variable (the top level showed its old attributes as a
/// residual, clp(Z)'s <c>_A in inf..sup</c>), and a goal the hook returned
/// that names it sees the value.</summary>
public sealed class VerifyAttributesProxyTests
{
    private const string Hooked = """
        :- module(vap, [watch/1]).
        watch(X) :- put_attr(X, vap, on).
        verify_attributes(Var, Value, [same(Var, Value)]).
        same(A, B) :- A == B.
        """;

    private static void Succeeds(PrologEngine e, string goal)
        => Assert.True(e.Query(goal).Success, goal);

    [Fact]
    public void NoLiveAttributedVariableOutlivesTheHook()
    {
        var e = new PrologEngine();
        e.ConsultString(Hooked);
        Succeeds(e, "watch(X), X = 1, '$live_attvars'(L), L == [].");
        Succeeds(e, "watch(X), watch(Y), X = Y, '$live_attvars'(L), L == [X].");
    }

    [Fact]
    public void AGoalTheHookReturnedSeesTheProxyBound()
    {
        var e = new PrologEngine();
        e.ConsultString(Hooked);
        Succeeds(e, "watch(X), X = f(a), X == f(a).");
    }

    [Fact]
    public void BacktrackingRestoresTheAttributedVariable()
    {
        var e = new PrologEngine();
        e.ConsultString(Hooked);
        Succeeds(e, "watch(X), ( X = 1, fail ; true ), get_attr(X, vap, on).");
    }
}
