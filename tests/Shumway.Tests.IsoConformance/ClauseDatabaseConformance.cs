using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §8.9 Clause creation and destruction.
///
/// Covers <c>asserta/1</c> (§8.9.1), <c>assertz/1</c> (§8.9.2),
/// <c>retract/1</c> (§8.9.3) and <c>abolish/1</c> (§8.9.4). Phase-9
/// chunk 131e already converted every contract-violation site to a
/// catchable ISO error; this chunk pins those, plus the ISO-mandated
/// re-satisfiability of <c>retract</c>, the
/// <c>permission_error(modify, static_procedure, _)</c> on a static
/// predicate, and <c>abolish</c>'s effect on the dynamic registry.
/// </summary>
public class ClauseDatabaseConformance
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ---------- §8.9.1 asserta/1 ----------

    [Fact]
    public void Asserta_PrependsClause()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic p/1.");
        Assert.True(e.Query(
            "assertz(p(1)), assertz(p(2)), asserta(p(0)).").Success);
        var xs = e.QueryAll("p(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(0), Int(1), Int(2) }, xs);
    }

    [Fact]
    public void Asserta_OnStatic_RaisesPermissionError()
    {
        var e = new PrologEngine();
        e.ConsultString("p(static_fact).");
        var sol = e.Query(
            "catch(asserta(p(_)), error(permission_error(Op, ObjT, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("modify"), sol["Op"]);
        Assert.Equal(Atom("static_procedure"), sol["ObjT"]);
    }

    [Fact]
    public void Asserta_VarClause_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(asserta(_C), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void Asserta_NonCallableHead_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(asserta(123), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("callable"), sol["T"]);
    }

    // ---------- §8.9.2 assertz/1 ----------

    [Fact]
    public void Assertz_AppendsClause()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic p/1.");
        Assert.True(e.Query(
            "assertz(p(1)), assertz(p(2)), assertz(p(3)).").Success);
        var xs = e.QueryAll("p(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(2), Int(3) }, xs);
    }

    [Fact]
    public void Assertz_RuleClause()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic q/1.");
        Assert.True(e.Query(
            "assertz((q(X) :- X > 0)), q(5).").Success);
        Assert.False(e.Query("q(-1).").Success);
    }

    [Fact]
    public void Assertz_OnStatic_RaisesPermissionError()
    {
        var e = new PrologEngine();
        e.ConsultString("p(static_fact).");
        var sol = e.Query(
            "catch(assertz(p(_)), error(permission_error(_, ObjT, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("static_procedure"), sol["ObjT"]);
    }

    // ---------- §8.9.3 retract/1 ----------

    [Fact]
    public void Retract_RemovesClause()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)), assertz(d(2)), assertz(d(3)).");
        Assert.True(e.Query("retract(d(2)).").Success);

        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(3) }, xs);
    }

    [Fact]
    public void Retract_IsResatisfiable()
    {
        // ISO §8.9.3 — retract is re-satisfiable: on backtracking it
        // visits the next matching clause. So `retract(d(_)), fail ;
        // true` retracts every matching clause.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)), assertz(d(2)), assertz(d(3)).");
        Assert.True(e.Query("(retract(d(_)), fail ; true).").Success);
        Assert.False(e.Query("d(_).").Success);
    }

    [Fact]
    public void Retract_NoMatch_Fails()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)).");
        Assert.False(e.Query("retract(d(99)).").Success);
    }

    [Fact]
    public void Retract_OnStatic_RaisesPermissionError()
    {
        var e = new PrologEngine();
        e.ConsultString("p(1).");
        var sol = e.Query(
            "catch(retract(p(1)), error(permission_error(_, ObjT, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("static_procedure"), sol["ObjT"]);
    }

    // ---------- §8.9.4 abolish/1 ----------

    [Fact]
    public void Abolish_RemovesAllClauses()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic counter/1.");
        e.Query("assertz(counter(1)), assertz(counter(2)).");
        Assert.True(e.Query("abolish(counter/1).").Success);

        // After abolish, the predicate is *undefined* — ISO §8.9.4 says
        // it's removed from the database. A direct call raises
        // existence_error(procedure, counter/1); catch confirms that.
        var sol = e.Query(
            "catch(counter(_), error(existence_error(procedure, _), _), true).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Abolish_OnNeverDeclared_Succeeds()
    {
        // ISO: abolish on a never-defined predicate is a no-op success
        // (nothing to remove).
        var e = new PrologEngine();
        Assert.True(e.Query("abolish(no_such/3).").Success);
    }

    [Fact]
    public void Abolish_VarIndicator_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(abolish(_I), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void Abolish_BadIndicatorShape_RaisesTypeError()
    {
        // §8.9.4.3 — indicator must be Name/Arity.
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(abolish(foo), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("predicate_indicator"), sol["T"]);
    }

    // ---------- ISO §8.9 logical-update view ----------

    [Fact]
    public void SameQueryAssertz_VisibleToLaterGoal()
    {
        // ISO logical-update view (ADR-015): an assertz earlier in the
        // query is visible to a later direct call to the predicate.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic q/1.");
        Assert.True(e.Query("assertz(q(7)), q(7).").Success);
    }

    [Fact]
    public void Findall_OverFreshlyAsserted_SeesIt()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic q/1.");
        var sol = e.Query(
            "assertz(q(1)), assertz(q(2)), findall(X, q(X), L).");
        Assert.True(sol.Success);
        var elements = new List<Term>();
        var list = sol["L"];
        while (list is CompoundTerm { Functor: ".", Args.Length: 2 } cons)
        {
            elements.Add(cons.Args[0]);
            list = cons.Args[1];
        }
        Assert.Equal(new[] { Int(1), Int(2) }, elements);
    }
}
