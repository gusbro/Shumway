using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What an answer says about a term it constrains AND binds. Two
/// things went wrong at once here.
///
/// <para>A constraint that an aliasing had DECIDED stayed on as a residual:
/// once P and Q are one variable, <c>dif(P-Q, 1-2)</c> is <c>dif(P-P, 1-2)</c>,
/// which no binding of P can ever violate, and showing it invites the reader
/// to look for a case that does not exist.</para>
///
/// <para>And a rational tree appeared twice under two names. copy_term/3
/// materialises a term and its attribute values in one pass so that what
/// occurs in both lands on one object: variables join by the name they were
/// read with, but a cycle has no variable to join by, so the projection built
/// a term of its own and the answer called the same thing <c>X</c> on one line
/// and <c>_C156</c> on the next.</para></summary>
public sealed class CyclicResidualDisplayTests
{
    /// <summary>The answer as the top level renders it, which is where the
    /// naming happens: the engine's own bindings say nothing about how two
    /// lines of one answer refer to each other.</summary>
    private static (PrologEngine Engine, string Answer) Ask(string query)
    {
        var e = new PrologEngine();
        e.UseCoroutining();
        var session = new TopLevelSession(e);
        using var run = session.StartQuery(query);
        Assert.True(run.MoveNext());
        return (e, run.Format(width: 200));
    }

    [Fact]
    public void AConstraintTheAliasingDecidedIsGone()
    {
        // P-P against 1-2 has no unifier at all, so the disequality holds for
        // good. Aliasing used to check only whether the two sides had become
        // identical, which is the violation, and never whether they had become
        // undeniable.
        var (_, answer) = Ask("dif(P-Q, 1-2), P = Q.");
        Assert.Equal("P = Q", answer);
    }

    [Fact]
    public void AConstraintStillOpenIsStillShown()
    {
        // The other half of the same rule: a disequality that a binding could
        // still violate has to survive the aliasing.
        var (_, answer) = Ask("dif(A, B), A = f(C).");
        Assert.Contains("dif(B, f(C))", answer);
    }

    [Theory]
    // The disequality is still sound, whichever way the aliasing goes.
    [InlineData("dif(P-Q, 1-2), P = Q, P = 1.", true)]     // 1-1 is not 1-2
    [InlineData("dif(X, Y), X = 1, Y = 2.", true)]
    [InlineData("dif(X, Y), X = 1, Y = 1.", false)]
    [InlineData("dif(M, N), M = N.", false)]
    public void RetiringOneNeverWeakensIt(string goal, bool holds)
    {
        var e = new PrologEngine();
        e.UseCoroutining();
        Assert.Equal(holds, e.Query(goal).Success);
    }

    [Fact]
    public void OneTermIsCalledOneThing()
    {
        // The answer binds X to a rational tree and constrains it. Both lines
        // are about the same tree, so both say X.
        var (_, answer) = Ask("dif(X, Y), -X = X.");
        Assert.Contains("X = - X", answer);
        Assert.Contains("dif(Y, X)", answer);
        Assert.DoesNotContain("_C", answer);
    }

    [Fact]
    public void ADeeperCycleIsNamedTheSameWay()
    {
        var (_, answer) = Ask("dif(A, B), A = A*B, B = C*A*C.");
        Assert.Contains("dif(C, A)", answer);
        Assert.DoesNotContain("_C", answer);
    }

    [Fact]
    public void ACycleWithNoConstraintOnItIsUnchanged()
    {
        var (_, answer) = Ask("X = [a, b|X].");
        Assert.Equal("X = [a, b | X]", answer);
    }

    [Fact]
    public void AnOrdinaryResidualIsUnchanged()
    {
        // Nothing cyclic here, so nothing to name: the term is already the
        // shortest way to say itself.
        var (_, answer) = Ask("dif(P, Q), P = f(Q).");
        Assert.Contains("P = f(Q)", answer);
        Assert.Contains("dif(Q, f(Q))", answer);
    }

    [Fact]
    public void TheConstraintIsStillTheOneItShows()
    {
        // Naming is a display: the constraint the answer reports must still be
        // the constraint the engine holds, and violating it must still fail.
        var e = new PrologEngine();
        e.UseCoroutining();
        Assert.False(e.Query("dif(X, Y), -X = X, Y = X.").Success);
        Assert.True(e.Query("dif(X, Y), -X = X, Y = other.").Success);
    }
}
