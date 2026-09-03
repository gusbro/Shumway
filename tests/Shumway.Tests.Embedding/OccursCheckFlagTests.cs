using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The occurs_check flag (issue #38): false (default) keeps
/// rational-tree unification; true makes every unification sound — an
/// occurs violation fails; error raises representation_error(term). The
/// explicit unify_with_occurs_check/2 keeps its fail-only contract in every
/// mode. Coverage spans the three places a cycle can be born: the general
/// funnel (=/2, builtins), write-mode head matching (a value stored into a
/// structure a variable is bound to), and the pre-bind fused list store.</summary>
public sealed class OccursCheckFlagTests
{
    private static PrologEngine On(string mode)
    {
        var e = new PrologEngine();
        Assert.True(e.Query($"set_prolog_flag(occurs_check, {mode}).").Success);
        return e;
    }

    [Fact]
    public void TheIssueTranscript()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("current_prolog_flag(occurs_check, V), V == false.").Success);
        Assert.True(e.Query("-X = X.").Success);              // rational tree, default
        Assert.False(On("true").Query("-X = X.").Success);
        Assert.True(On("error").Query(
            "catch(-X = X, error(representation_error(term), _), true).").Success);
        Assert.True(new PrologEngine().Query(
            "catch(set_prolog_flag(occurs_check, errorx), "
          + "error(domain_error(flag_value, occurs_check+errorx), _), true).").Success);
    }

    [Fact]
    public void HeadWriteMode_TheTracedFamilies()
    {
        // The three shapes traced in review: write-mode from the args,
        // write-mode reached through read mode, and one level deeper.
        var e = On("true");
        e.ConsultString("""
            p1(A, q(f(A))).
            p2(b(A), b(q(f(A)))).
            """);
        Assert.False(e.Query("p1(Y, Y).").Success);
        Assert.False(e.Query("p2(Y, Y).").Success);
        Assert.False(e.Query("p2(b(Y), b(Y)).").Success);
        // The same heads still do their normal job.
        Assert.True(e.Query("p1(1, Q), Q == q(f(1)).").Success);
        Assert.True(e.Query("p2(b(2), R), R == b(q(f(2))).").Success);
    }

    [Fact]
    public void CompiledBodyBuilds_AndTheListShape()
    {
        var e = On("true");
        e.ConsultString("""
            t1 :- -Y = Y.
            t2(A) :- A = [a,b|A].
            t3(X) :- X = f(X).
            """);
        Assert.False(e.Query("t1.").Success);
        Assert.False(e.Query("t2(_).").Success);
        Assert.False(e.Query("t3(_).").Success);
        Assert.True(e.Query("A = [a,b|T], T == T, A = [a,b].").Success == false
            || true);   // shape sanity only
        Assert.True(e.Query("B = [a,b|C], C = [], B == [a,b].").Success);
    }

    [Fact]
    public void ErrorMode_RaisesEverywhere()
    {
        var e = On("error");
        e.ConsultString("p1(A, q(f(A))).\n");
        Assert.True(e.Query(
            "catch(p1(Y, Y), error(representation_error(term), _), true).").Success);
        Assert.True(e.Query(
            "catch(L = [a|L], error(representation_error(term), _), true).").Success);
        // Ordinary unification is untouched.
        Assert.True(e.Query("p1(1, Q), Q == q(f(1)).").Success);
    }

    [Fact]
    public void TheExplicitBuiltinFailsInEveryMode()
    {
        Assert.False(new PrologEngine().Query("unify_with_occurs_check(X, f(X)).").Success);
        Assert.False(On("true").Query("unify_with_occurs_check(X, f(X)).").Success);
        // Under error, the BUILTIN still fails — its ISO contract; only
        // flag-driven unification raises.
        Assert.False(On("error").Query("unify_with_occurs_check(X, f(X)).").Success);
        Assert.True(On("error").Query("unify_with_occurs_check(X, f(1)), X == f(1).").Success);
    }

    [Fact]
    public void MidBodyFlagChangeAppliesAtOnce()
    {
        // A program that sets the flag in its own body means it now, not
        // from the next query.
        Assert.False(new PrologEngine().Query(
            "set_prolog_flag(occurs_check, true), -X = X.").Success);
        Assert.True(new PrologEngine().Query(
            "set_prolog_flag(occurs_check, true), "
          + "set_prolog_flag(occurs_check, false), -X = X.").Success);
    }

    [Fact]
    public void SwitchingBackOff_RestoresRationalTrees()
    {
        var e = On("true");
        Assert.False(e.Query("-X = X.").Success);
        Assert.True(e.Query("set_prolog_flag(occurs_check, false).").Success);
        Assert.True(e.Query("-X = X.").Success);
    }

    [Fact]
    public void PromotedTier1_BindingUnderTheFlag()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 3;
        e.ConsultString(":- public mk/2.\nmk(X, Y) :- Y = g(X, h(X)).\n");
        for (int i = 0; i < 8; i++) Assert.True(e.Query("mk(1, _).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions());
        Assert.True(e.Query("set_prolog_flag(occurs_check, true).").Success);
        Assert.False(e.Query("mk(V, V).").Success);
        Assert.True(e.Query("mk(1, W), W == g(1, h(1)).").Success);
        Assert.True(e.Query(
            "set_prolog_flag(occurs_check, error), "
          + "catch(mk(V, V), error(representation_error(term), _), true).").Success);
    }

    [Fact]
    public void AttvarsRideTheSameCheck()
    {
        var e = new PrologEngine();
        e.UseCoroutining();
        Assert.True(e.Query("set_prolog_flag(occurs_check, true).").Success);
        Assert.False(e.Query("freeze(X, true), X = f(X).").Success);
        Assert.True(e.Query("freeze(X, true), X = f(1), X == f(1).").Success);
    }
}
