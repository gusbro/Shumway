using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 (Logtalk bring-up): <c>subsumes_term/2</c> (ISO §8.2.4), needed by
/// the Logtalk <c>term</c> library object. A pure test — it must not leave any
/// variable bound.
/// </summary>
public class SubsumesTermTests
{
    private static bool Holds(string goal) => new PrologEngine().Query(goal).Success;

    [Fact] public void GeneralSubsumesInstance() =>
        Assert.True(Holds("subsumes_term(f(_X), f(a))."));

    [Fact] public void InstanceDoesNotSubsumeGeneral() =>
        Assert.False(Holds("subsumes_term(f(a), f(_X))."));

    [Fact] public void SharedVarBlocksSubsumption() =>
        Assert.False(Holds("subsumes_term(f(X, X), f(b, c))."));

    [Fact] public void DistinctVarsSubsumeGroundPair() =>
        Assert.True(Holds("subsumes_term(g(_P, _Q), g(1, 2))."));

    [Fact] public void VariableSubsumesAnything() =>
        Assert.True(Holds("subsumes_term(_X, f(a, b, c))."));

    [Fact] public void IdenticalTermsSubsume() =>
        Assert.True(Holds("subsumes_term(foo(a, b), foo(a, b))."));

    [Fact]
    public void DoesNotBindVariables()
    {
        // After a successful subsumes_term, the General term's variables stay
        // unbound (it is a test, not a unification).
        var sol = new PrologEngine().Query(
            "X = f(Z), subsumes_term(X, f(a)), var(Z), X == f(Z).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void SpecificVarNotBound()
    {
        var sol = new PrologEngine().Query(
            "subsumes_term(f(_G), f(Y)), var(Y).");
        Assert.True(sol.Success);
    }
}
