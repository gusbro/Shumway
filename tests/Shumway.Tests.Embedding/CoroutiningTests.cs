using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The coroutining library: freeze/2, frozen/2, dif/2, plus the core
/// term_attvars/2 builtin and the trial-unification wakeup hygiene it
/// depends on. The library rides the multifile verify_attributes/4 hook,
/// so it must coexist with CLP(FD) on one engine.
/// </summary>
public class CoroutiningTests
{
    private static PrologEngine Co()
    {
        var e = new PrologEngine();
        e.UseCoroutining();
        return e;
    }

    // ===== freeze/2 =====

    [Fact]
    public void Freeze_OnBoundVar_RunsImmediately()
    {
        var sol = Co().Query("X = 1, freeze(X, Y = ran).");
        Assert.True(sol.Success);
        Assert.Equal("ran", sol["Y"]!.ToString());
    }

    [Fact]
    public void Freeze_WakesWhenVarIsBound()
    {
        var sol = Co().Query("freeze(X, Y = woke), X = 1.");
        Assert.True(sol.Success);
        Assert.Equal("woke", sol["Y"]!.ToString());
    }

    [Fact]
    public void Freeze_UnboundVar_StaysSuspended()
    {
        // The goal would fail if run — but the variable is never bound.
        Assert.True(Co().Query("freeze(_X, fail).").Success);
    }

    [Fact]
    public void Freeze_FailingGoal_FailsTheBinding()
    {
        Assert.False(Co().Query("freeze(X, fail), X = 1.").Success);
    }

    [Fact]
    public void Freeze_MultipleGoals_RunInFreezeOrder()
    {
        var sol = Co().Query(
            "freeze(X, assertz(co_log(a))), freeze(X, assertz(co_log(b))), "
            + "X = 1, findall(K, co_log(K), Ks), Ks == [a, b].");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Freeze_VarVarAliasing_MigratesTheGoal()
    {
        var sol = Co().Query("freeze(X, Y = woke), X = Z, Z = 1.");
        Assert.True(sol.Success);
        Assert.Equal("woke", sol["Y"]!.ToString());
    }

    [Fact]
    public void Freeze_BacktrackingRestoresTheSuspension()
    {
        // The frozen fail kills both alternatives — the suspension must be
        // re-armed after the first binding is undone.
        var sols = Co().QueryAll("freeze(X, fail), ( X = 1 ; X = 2 ).").ToList();
        Assert.Empty(sols);
    }

    // ===== frozen/2 =====

    [Fact]
    public void Frozen_ReadsBackTheDelayedGoal()
    {
        var sol = Co().Query("freeze(X, foo(1)), frozen(X, G).");
        Assert.True(sol.Success);
        Assert.Equal("foo(1)", sol["G"]!.ToString());
    }

    [Fact]
    public void Frozen_PlainVar_IsTrue()
    {
        var sol = Co().Query("frozen(_X, G).");
        Assert.True(sol.Success);
        Assert.Equal("true", sol["G"]!.ToString());
    }

    // ===== dif/2 =====

    [Fact]
    public void Dif_GroundDifferent_Succeeds() =>
        Assert.True(Co().Query("dif(a, b).").Success);

    [Fact]
    public void Dif_GroundEqual_Fails() =>
        Assert.False(Co().Query("dif(a, a).").Success);

    [Fact]
    public void Dif_SameVariable_Fails() =>
        Assert.False(Co().Query("dif(X, X).").Success);

    [Fact]
    public void Dif_SuspendsAndFailsOnEqualBinding()
    {
        Assert.False(Co().Query("dif(X, a), X = a.").Success);
        Assert.True(Co().Query("dif(X, a), X = b.").Success);
    }

    [Fact]
    public void Dif_CompoundArgs_ResolveArgByArg()
    {
        // X = a leaves the pair unifiable only via Y = b; Y = c settles it.
        Assert.True(Co().Query("dif(f(X, Y), f(a, b)), X = a, Y = c.").Success);
        Assert.False(Co().Query("dif(f(X, Y), f(a, b)), X = a, Y = b.").Success);
    }

    [Fact]
    public void Dif_AliasingChain_FailsWhenIdentical()
    {
        Assert.False(Co().Query("dif(X, Y), X = Z, Y = Z.").Success);
    }

    [Fact]
    public void Dif_PrunesTheEqualAlternative()
    {
        var sols = Co().QueryAll("dif(X, 1), ( X = 1 ; X = 2 ).").ToList();
        var sol = Assert.Single(sols);
        Assert.Equal("2", sol["X"]!.ToString());
    }

    [Fact]
    public void Dif_RationalTree_ResolvesOnBinding()
    {
        // X = a makes the pair (a, f(a)) — not unifiable, dif holds.
        Assert.True(Co().Query("dif(X, f(X)), X = a.").Success);
    }

    // ===== term_attvars/2 (core builtin, no library needed) =====

    [Fact]
    public void TermAttvars_CollectsTheRealAttributedVariables()
    {
        var sol = new PrologEngine().Query(
            "put_attr(X, m, v), term_attvars(s(a, X, [X|_Y]), Vs), Vs = [W], W == X.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void TermAttvars_NoAttvars_GivesEmptyList()
    {
        var sol = new PrologEngine().Query("term_attvars(f(_X, g(_Y), 3), Vs).");
        Assert.True(sol.Success);
        Assert.Equal("[]", sol["Vs"]!.ToString());
    }

    // ===== trial-unification wakeup hygiene =====

    [Fact]
    public void NotUnifiable_DiscardsWakeupsFromTheFailedTrial()
    {
        // The trial binds X to 2 (queueing X's clpfd hook) before failing on
        // a \= b. The queued wakeup must die with the trial: were it run at
        // the next goal boundary, verify_attributes(clpfd, fd(5..9), 2)
        // would fail the query even though X was never really bound.
        var e = new PrologEngine();
        e.UseClpfd();
        Assert.True(e.Query(@"X in 5..9, f(X, a) \= f(2, b), X = 7.").Success);
    }

    // ===== coexistence with CLP(FD) =====

    [Fact]
    public void Coroutining_AndClpfd_ShareOneEngine()
    {
        var e = Co();
        e.UseClpfd();
        var sol = e.Query(
            "X in 1..5, freeze(Y, Z = woke), X #> 3, Y = go, label([X]).");
        Assert.True(sol.Success);
        Assert.Equal("4", sol["X"]!.ToString());
        Assert.Equal("woke", sol["Z"]!.ToString());
    }

    [Fact]
    public void Freeze_OnAClpfdVariable_BothHooksFire()
    {
        var e = Co();
        e.UseClpfd();
        var sol = e.Query("X in 1..3, freeze(X, Y = woke), X = 2.");
        Assert.True(sol.Success);
        Assert.Equal("woke", sol["Y"]!.ToString());
        // And the domain check still guards: a value outside fails.
        Assert.False(e.Query("A in 1..3, freeze(A, true), A = 9.").Success);
    }

    // ===== residual projection =====

    [Fact]
    public void Freeze_ProjectsAsResidualGoal()
    {
        var sol = Co().Query("freeze(X, foo(X)), copy_term(X, _C, Gs), Gs = [G].");
        Assert.True(sol.Success);
        Assert.StartsWith("freeze(", sol["G"]!.ToString());
    }

    [Fact]
    public void Dif_ProjectsAsDifGoal()
    {
        var sol = Co().Query("dif(X, a), copy_term(X, _C, Gs), Gs = [G].");
        Assert.True(sol.Success);
        Assert.StartsWith("dif(", sol["G"]!.ToString());
    }

    // ===== call_residue_vars/2 =====

    [Fact]
    public void CallResidueVars_CapturesTheSuspendedVariable()
    {
        // dif(X, a) leaves X constrained — it is the residue.
        var sol = Co().Query("call_residue_vars(dif(X, a), Vs), Vs = [V], V == X.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void CallResidueVars_NoConstraint_GivesEmptyList()
    {
        var sol = Co().Query("call_residue_vars(X = 1, Vs).");
        Assert.True(sol.Success);
        Assert.Equal("[]", sol["Vs"]!.ToString());
    }

    [Fact]
    public void CallResidueVars_ResolvedConstraint_LeavesNoResidue()
    {
        // dif holds and then X is bound to a different value — no residue left.
        var sol = Co().Query("call_residue_vars((dif(X, a), X = b), Vs).");
        Assert.True(sol.Success);
        Assert.Equal("[]", sol["Vs"]!.ToString());
    }

    [Fact]
    public void CallResidueVars_IgnoresPreExistingConstraints()
    {
        // The X constraint predates the call — only Y's residue is reported.
        var sol = Co().Query(
            "dif(X, a), call_residue_vars(dif(Y, b), Vs), Vs = [V], V == Y.");
        Assert.True(sol.Success);
    }
}
