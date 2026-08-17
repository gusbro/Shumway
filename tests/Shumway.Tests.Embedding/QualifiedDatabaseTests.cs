using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Stage 3 of the M:P story — the database side. Dynamics are
/// flat-global (invariant), so a <c>Module:</c> qualifier on
/// assert/retract/retractall/abolish VALIDATES and DROPS: every module
/// reaches the one shared store. Assert accepts the qualifier around the
/// whole clause, around a rule, and around a rule's head; retract peels the
/// pattern ON THE HEAP so the caller's variables keep binding (a copy would
/// silently stop); abolish takes both indicator spellings. Errors pinned:
/// variable module → instantiation_error, non-atom → type_error(atom).</summary>
public sealed class QualifiedDatabaseTests
{
    [Fact]
    public void QualifiedAssert_LandsInTheOneFlatStore()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("assertz(qd_a:f(1)).").Success);
        Assert.True(e.Query("assertz(user:f(2)).").Success);
        Assert.True(e.Query("asserta(other:f(0)).").Success);
        // One store, whatever the qualifier said — and asserta prepended.
        Assert.True(e.Query("findall(X, f(X), [0, 1, 2]).").Success);
    }

    [Fact]
    public void QualifiedAssert_AllThreeRuleSpellings()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("assertz(m:(r1(X) :- X > 0)).").Success);
        Assert.True(e.Query("assertz((m:r2(X) :- X > 0)).").Success);
        Assert.True(e.Query("assertz(m1:m2:(r3(X) :- X > 0)).").Success);
        Assert.True(e.Query("r1(5), r2(5), r3(5).").Success);
    }

    [Fact]
    public void QualifiedRetract_BindsTheCallersVariables()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(qd_r/1).\nqd_r(10).\nqd_r(20).\n");
        // The heap-level peel keeps the pattern's variables the caller's own:
        // X must come back bound.
        var sol = e.Query("retract(qd_r:qd_r(X)).");
        Assert.True(sol.Success);
        Assert.Equal("10", sol["X"]!.ToString());
        Assert.True(e.Query("findall(X, qd_r(X), [20]).").Success);
    }

    [Fact]
    public void QualifiedRetract_RuleForm()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("assertz(m:(qd_w(X) :- X > 3)).").Success);
        Assert.True(e.Query("retract(m:(qd_w(Y) :- B)), B == (Y > 3).").Success);
        Assert.False(e.Query("qd_w(9).").Success);
    }

    [Fact]
    public void QualifiedRetractall_PeelsAndSweeps()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(qd_s/1).\nqd_s(1).\nqd_s(2).\n");
        Assert.True(e.Query("retractall(anything:qd_s(_)).").Success);
        Assert.False(e.Query("qd_s(_).").Success);
    }

    [Fact]
    public void QualifiedAbolish_BothIndicatorSpellings()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("assertz(qd_k(1)), abolish(m:qd_k/1), \\+ qd_k(_).").Success);
        Assert.True(e.Query("assertz(qd_j(1)), abolish(m:(qd_j/1)), \\+ qd_j(_).").Success);
    }

    [Fact]
    public void QualifierErrors_AreTheIsoOnes()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(assertz(V:q(1)), error(instantiation_error, _), true).").Success);
        Assert.True(e.Query(
            "catch(assertz(1:q(1)), error(type_error(atom, _), _), true).").Success);
        Assert.True(e.Query(
            "catch(retract(1:q(_)), error(type_error(atom, _), _), true).").Success);
        Assert.True(e.Query(
            "catch(retractall(V2:q(_)), error(instantiation_error, _), true).").Success);
        Assert.True(e.Query(
            "catch(abolish(1:q/1), error(type_error(atom, _), _), true).").Success);
    }

    [Fact]
    public void ModuleLocalStatic_StaysProtected_ThroughTheQualifier()
    {
        var e = new PrologEngine();
        e.ConsultString(":- module(qd_m, []).\nqd_loc(1).\n");
        // Same protection as the bare spelling: the local's name resolves to
        // the module's STATIC predicate — no quiet bare dynamic minted over it.
        Assert.True(e.Query(
            "catch(assertz(qd_m:qd_loc(9)), "
            + "error(permission_error(modify, static_procedure, _), _), true).").Success);
        Assert.True(e.Query(
            "catch(retract(qd_m:qd_loc(_)), "
            + "error(permission_error(modify, static_procedure, _), _), true).").Success);
    }

    [Fact]
    public void UnqualifiedForms_Untouched()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("assertz(qd_p(1)), retract(qd_p(1)), \\+ qd_p(_).").Success);
        Assert.True(e.Query("assertz(qd_q(1)), retractall(qd_q(_)), \\+ qd_q(_).").Success);
    }
}
