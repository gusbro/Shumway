using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 88 (Phase 6): a <c>!</c> written inside a runtime compound goal
/// passed to <c>call/1</c> now commits — it cuts back to the choice-point
/// level the enclosing <c>call</c> established. Before this it was a
/// no-op, which is unsound: backtracking re-ran clauses ISO would have
/// cut away, re-executing their side effects. The cut stays local to the
/// <c>call</c> — it never reaches the caller's choice points.
/// </summary>
public class Chunk88Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ---- the cut commits inside a call'd conjunction ----

    [Fact]
    public void CutInCalledConjunction_CommitsToFirstSolution()
    {
        var engine = new PrologEngine();
        engine.ConsultString("m(1).\nm(2).\nm(3).\n");
        var xs = engine.QueryAll("call((m(X), !)).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1) }, xs);
    }

    [Fact]
    public void CutInCalledConjunction_SideEffectOfCutAwayClauseDoesNotRun()
    {
        // The soundness case: a(2)'s body asserts a fact. Committing to
        // a(1) with the cut must stop a(2) from ever running — otherwise
        // a non-backtrackable assertz corrupts the database.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- dynamic ran/0.\n" +
            "a(1).\n" +
            "a(2) :- assertz(ran).\n");
        var xs = engine.QueryAll("call((a(X), !)).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1) }, xs);
        Assert.False(engine.Query("ran.").Success);
    }

    [Fact]
    public void CutInCalledConjunction_LaterGoalStillRuns()
    {
        // The cut commits a(X) but the conjunction continues past it.
        var engine = new PrologEngine();
        engine.ConsultString("a(1).\na(2).\n");
        var sol = engine.Query("call((a(X), !, Y = done)).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["X"]);
        Assert.Equal(Atom("done"), sol["Y"]);
    }

    // ---- the cut stays local to the call ----

    [Fact]
    public void CutInCall_DoesNotCutParentChoicePoints()
    {
        // The `!` is inside call/1, so it commits the call's goal only —
        // the enclosing m(X) choice point keeps backtracking.
        var engine = new PrologEngine();
        engine.ConsultString("m(1).\nm(2).\nm(3).\n");
        var xs = engine.QueryAll("m(X), call((!, true)).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(2), Int(3) }, xs);
    }

    [Fact]
    public void BareCallCut_IsStillANoOp()
    {
        // call(!) on its own cuts to the call's own entry — nothing.
        var engine = new PrologEngine();
        engine.ConsultString("m(1).\nm(2).\n");
        var xs = engine.QueryAll("m(X), call(!).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(2) }, xs);
    }

    [Fact]
    public void CutInCall_InsideClauseBody_DoesNotCutTheClause()
    {
        // p/1's body is a disjunction; a `!` inside a call in the first
        // branch must not cut it, so all three answers are produced.
        var engine = new PrologEngine();
        engine.ConsultString(
            "q(a).\nq(b).\n" +
            "p(R) :- ( q(R), call((!, true)) ; R = other ).\n");
        var rs = engine.QueryAll("p(R).").Select(s => s["R"]).ToList();
        Assert.Equal(new[] { Atom("a"), Atom("b"), Atom("other") }, rs);
    }

    // ---- the cut through other control constructs ----

    [Fact]
    public void CutInCalledDisjunct_CommitsTheDisjunction()
    {
        // A `!` in the first disjunct cuts the disjunction's own choice
        // point as well as m/1's, so X = 99 is never reached.
        var engine = new PrologEngine();
        engine.ConsultString("m(1).\nm(2).\nm(3).\n");
        var xs = engine.QueryAll("call(( (m(X), !) ; X = 99 )).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1) }, xs);
    }

    [Fact]
    public void CalledIfThenElse_TakesThenBranch()
    {
        var engine = new PrologEngine();
        engine.ConsultString("m(1).\nm(2).\n");
        var sol = engine.Query("call((m(X) -> Y = t ; Y = e)).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["X"]);
        Assert.Equal(Atom("t"), sol["Y"]);
    }

    [Fact]
    public void CalledIfThenElse_TakesElseBranch()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("call((fail -> Y = t ; Y = e)).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("e"), sol["Y"]);
    }

    [Fact]
    public void CutInThenBranch_CommitsThroughTheCall()
    {
        // A `!` in the THEN branch is cut-transparent: it commits the
        // choice points created inside the call (m/1's), so only X = 1.
        var engine = new PrologEngine();
        engine.ConsultString("m(1).\nm(2).\nm(3).\n");
        var xs = engine.QueryAll("call((m(X), (true -> ! ; true))).")
            .Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1) }, xs);
    }

    [Fact]
    public void CalledNegation_Works()
    {
        var engine = new PrologEngine();
        engine.ConsultString("m(1).\n");
        Assert.True(engine.Query("call(\\+ m(2)).").Success);
        Assert.False(engine.Query("call(\\+ m(1)).").Success);
    }

    [Fact]
    public void CutInNestedCall_StillCommits()
    {
        var engine = new PrologEngine();
        engine.ConsultString("m(1).\nm(2).\nm(3).\n");
        var xs = engine.QueryAll("call(call((m(X), !))).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1) }, xs);
    }
}
