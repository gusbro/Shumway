using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The database side of <c>M:P</c>. A <c>Module:</c> qualifier on
/// assert/retract/retractall/abolish names the module whose predicate the
/// operation reaches (ADR-038, amendment): <c>user:</c> the global store, a
/// module of the modern world its own, a module nothing defined yet is
/// created. Assert accepts the qualifier around the whole clause, around a
/// rule, and around a rule's head; retract peels the pattern on the heap so
/// the caller's variables keep binding (a copy would silently stop); abolish
/// takes both indicator spellings. Errors pinned: variable module →
/// instantiation_error, non-atom → type_error(atom).</summary>
public sealed class QualifiedDatabaseTests
{
    [Fact]
    public void QualifiedAssert_NamesTheModulesStore()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("assertz(qd_a:f(1)).").Success);
        Assert.True(e.Query("assertz(user:f(2)).").Success);
        Assert.True(e.Query("asserta(qd_a:f(0)).").Success);
        // user: is the global store; qd_a's is its own, and asserta prepended.
        Assert.True(e.Query("findall(X, f(X), [2]).").Success);
        Assert.True(e.Query("findall(X, qd_a:f(X), [0, 1]).").Success);
    }

    [Fact]
    public void QualifiedAssert_AllThreeRuleSpellings()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("assertz(m:(r1(X) :- X > 0)).").Success);
        Assert.True(e.Query("assertz((m:r2(X) :- X > 0)).").Success);
        // Nested qualification: the innermost module is the clause's.
        Assert.True(e.Query("assertz(m1:m2:(r3(X) :- X > 0)).").Success);
        Assert.True(e.Query("m:r1(5), m:r2(5), m2:r3(5).").Success);
        Assert.True(e.Query("catch(r1(5), error(existence_error(procedure, r1/1), _), true).").Success);
    }

    [Fact]
    public void QualifiedRetract_BindsTheCallersVariables()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(qd_r/1).\nqd_r(10).\nqd_r(20).\n");
        // The heap-level peel keeps the pattern's variables the caller's own:
        // X must come back bound.
        var sol = e.Query("retract(user:qd_r(X)).");
        Assert.True(sol.Success);
        Assert.Equal("10", sol["X"]!.ToString());
        Assert.True(e.Query("findall(X, qd_r(X), [20]).").Success);
    }

    [Fact]
    public void QualifiedRetract_IsResatisfiable()
    {
        // The enumeration re-reads the pattern on every resume, qualifiers
        // and all; it used to stop after the first solution.
        var e = new PrologEngine();
        Assert.True(e.Query("assertz(f(1)), assertz(f(2)), assertz(f(3)), "
            + "findall(X, retract(user:f(X)), L), L == [1, 2, 3], \\+ f(_).").Success);
        Assert.True(e.Query("Db = qd_b, assertz(Db:d(1)), assertz(Db:d(2)), assertz(Db:d(3)), "
            + "bagof(X, retract(Db:d(X)), L), L == [1, 2, 3], \\+ Db:d(_).").Success);
        Assert.True(e.Query("assertz(qd_b:(r(1) :- true)), assertz(qd_b:(r(2) :- true)), "
            + "findall(X, retract((qd_b:r(X) :- true)), L), L == [1, 2].").Success);
    }

    [Fact]
    public void QualifiedRetract_RuleForm()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("assertz(m:(qd_w(X) :- X > 3)).").Success);
        Assert.True(e.Query("retract(m:(qd_w(Y) :- B)), B == (Y > 3).").Success);
        Assert.False(e.Query("m:qd_w(9).").Success);
        // The head-qualified spelling of the pattern reaches the same clause.
        Assert.True(e.Query("assertz(m:(qd_w(X) :- X > 3)), retract((m:qd_w(_) :- _)), \\+ m:qd_w(9).").Success);
    }

    [Fact]
    public void QualifiedRetractall_SweepsTheModulesStore()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(qd_s/1).\nqd_s(1).\nqd_s(2).\n");
        // Another module's store is another predicate: the global one stays.
        Assert.True(e.Query("retractall(anything:qd_s(_)).").Success);
        Assert.True(e.Query("findall(X, qd_s(X), [1, 2]).").Success);
        Assert.True(e.Query("retractall(user:qd_s(_)).").Success);
        Assert.False(e.Query("qd_s(_).").Success);
    }

    [Fact]
    public void QualifiedAbolish_BothIndicatorSpellings()
    {
        var e = new PrologEngine();
        // After abolish the predicate is undefined — a new call raises
        // existence_error (§8.9.4), it does not quietly fail.
        Assert.True(e.Query(
            "assertz(qd_k(1)), abolish(user:qd_k/1), "
            + "catch(qd_k(_), error(existence_error(procedure, qd_k/1), _), true).").Success);
        Assert.True(e.Query(
            "assertz(qd_j(1)), abolish(user:(qd_j/1)), "
            + "catch(qd_j(_), error(existence_error(procedure, qd_j/1), _), true).").Success);
        // Another module's indicator abolishes nothing of the global store.
        Assert.True(e.Query("assertz(qd_i(1)), abolish(m:qd_i/1), qd_i(1).").Success);
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
        // the module's static predicate — no quiet bare dynamic minted over it.
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
