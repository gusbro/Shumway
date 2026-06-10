using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 407 (Phase 29, ADR-021 candidate #2) — conservative meta-wrapper
/// unfolding (<c>MetaWrapperUnfold</c>). User-defined control wrappers
/// (Arity-compat <c>ifthen/2</c>, <c>ifthenelse/3</c>, not-via-cut-fail)
/// called with statically-known goals are unfolded into inline if-then-else at
/// the call site. These tests pin the SEMANTICS through the public engine —
/// the same programs must behave identically whether or not the unfold fires,
/// so every case here encodes the wrapper contract, including the edges where
/// a naive unfold would diverge (then-goal failure, cut opacity, lazy type
/// errors, runtime meta-calls to the wrapper's standalone form).
/// </summary>
public class Chunk407Tests
{
    private static PrologEngine Make(string source)
    {
        var engine = new PrologEngine();
        engine.ConsultString(source);
        return engine;
    }

    // ----- T2: Blint-style ifthen/2 (C -> !, T  +  catch-all) -----

    private const string IfThen =
        "ifthen(X,Y) :- X -> !, Y.\n"
        + "ifthen(_,_) :- !.\n"
        + ":- dynamic hit/1.\n";

    [Fact]
    public void IfThen_CondSucceeds_RunsThen()
    {
        var e = Make(IfThen + "go :- ifthen(true, assertz(hit(yes))).\n");
        Assert.True(e.Query("go.").Success);
        Assert.True(e.Query("hit(yes).").Success);
    }

    [Fact]
    public void IfThen_CondFails_SkipsThen_Succeeds()
    {
        var e = Make(IfThen + "go :- ifthen(fail, assertz(hit(no))).\n");
        Assert.True(e.Query("go.").Success);
        Assert.False(e.Query("hit(no).").Success);
    }

    [Fact]
    public void IfThen_ThenFails_WholeCallFails()
    {
        // The committed branch: C succeeded -> T's failure must FAIL the call
        // (the catch-all was cut away). A naive (C, T ; true) unfold would
        // wrongly succeed here.
        var e = Make(IfThen + "go :- ifthen(true, fail).\n");
        Assert.False(e.Query("go.").Success);
    }

    [Fact]
    public void IfThen_CondCommitsFirstSolution()
    {
        // C = member-like generator: ifthen commits to C's FIRST solution;
        // backtracking into go/1 must NOT re-enter the condition.
        var e = Make(IfThen
            + "pick(1).\npick(2).\npick(3).\n"
            + "go(X) :- ifthen(pick(X), true).\n");
        var all = e.QueryAll("go(X).").ToList();
        Assert.Single(all);
        Assert.Equal(1, all[0].Get<int>("X"));
    }

    [Fact]
    public void IfThen_CutInsidePassedGoal_StaysOpaque()
    {
        // A ! inside the goal the CALLER passes is opaque both ways (meta-call
        // barrier / ISO condition opacity): the caller's own choice points
        // survive. outer/1 enumerates both solutions.
        var e = Make(IfThen
            + "pick(1).\npick(2).\n"
            + "outer(X) :- pick(X), ifthen((true, !), true).\n");
        var all = e.QueryAll("outer(X).").ToList();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void IfThen_CommaCutVariant_SameSemantics()
    {
        var e = Make(
            "andthen(X,Y) :- X, !, Y.\n"
            + "andthen(_,_) :- !.\n"
            + "go1 :- andthen(true, true).\n"
            + "go2 :- andthen(fail, fail).\n"
            + "go3 :- andthen(true, fail).\n");
        Assert.True(e.Query("go1.").Success);
        Assert.True(e.Query("go2.").Success);
        Assert.False(e.Query("go3.").Success);
    }

    // ----- T1: single-clause pure-control wrappers -----

    [Fact]
    public void IfThenElse_BothBranches()
    {
        var e = Make(
            "ifthenelse(X,Y,Z) :- X -> Y ; Z.\n"
            + ":- dynamic got/1.\n"
            + "go(C) :- ifthenelse(C, assertz(got(then)), assertz(got(else))).\n");
        Assert.True(e.Query("go(true).").Success);
        Assert.True(e.Query("got(then).").Success);
        Assert.False(e.Query("got(else).").Success);
        Assert.True(e.Query("retractall(got(_)), go(fail).").Success);
        Assert.True(e.Query("got(else).").Success);
    }

    [Fact]
    public void OrElse_SingleClauseDisjunction()
    {
        var e = Make(
            "orelse(X,Y) :- X ; Y.\n"
            + "go(R) :- orelse(R = a, R = b).\n");
        var all = e.QueryAll("go(R).").ToList();
        Assert.Equal(2, all.Count);   // both disjuncts enumerable
    }

    // ----- T3: negation-by-cut-fail -----

    [Fact]
    public void NotViaCutFail_BothOutcomes()
    {
        var e = Make(
            "mynot(X) :- X, !, fail.\n"
            + "mynot(_).\n"
            + "yes :- mynot(fail).\n"
            + "no :- mynot(true).\n");
        Assert.True(e.Query("yes.").Success);
        Assert.False(e.Query("no.").Success);
    }

    // ----- guards: where the unfold must NOT change behaviour -----

    [Fact]
    public void VariableGoalArg_StillDispatchesAtRuntime()
    {
        // The call site passes a VARIABLE goal — no unfold; the wrapper's
        // standalone form must meta-call it.
        var e = Make(IfThen + "go(G) :- ifthen(G, assertz(hit(ran))).\n");
        Assert.True(e.Query("go(true).").Success);
        Assert.True(e.Query("hit(ran).").Success);
    }

    [Fact]
    public void RuntimeMetaCall_ReachesStandaloneWrapper()
    {
        // call/1 with a runtime-built wrapper goal: the wrapper predicate must
        // still exist standalone even when every static site was unfolded.
        var e = Make(IfThen + "go :- ifthen(true, true).\n");
        Assert.True(e.Query("G = ifthen(true, assertz(hit(meta))), call(G).").Success);
        Assert.True(e.Query("hit(meta).").Success);
    }

    [Fact]
    public void NonCallableArg_KeepsLazyTypeError()
    {
        // ifthen(1, true): the original raises type_error(callable) AT RUN TIME
        // inside the wrapper. The unfold skips non-callable args, so the error
        // surfaces exactly as before (catchable, not a compile-time failure).
        var e = Make(IfThen + "go :- ifthen(1, true).\n");
        Assert.True(e.Query("catch(go, error(type_error(callable, _), _), true).").Success);
    }

    [Fact]
    public void NonTemplatePredicate_NotUnfolded()
    {
        // Three clauses / side-effecting body — not a wrapper; must behave as
        // plain clauses (first-solution semantics preserved).
        var e = Make(
            ":- dynamic log/1.\n"
            + "almost(X,Y) :- X -> !, Y.\n"
            + "almost(_,_) :- assertz(log(fallback)).\n"
            + "almost(_,_) :- !.\n"
            + "go :- almost(fail, true).\n");
        Assert.True(e.Query("go.").Success);
        Assert.True(e.Query("log(fallback).").Success);
    }

    [Fact]
    public void NestedWrapperArguments_UnfoldRecursively()
    {
        var e = Make(IfThen
            + "go :- ifthen(true, ifthen(true, assertz(hit(nested)))).\n");
        Assert.True(e.Query("go.").Success);
        Assert.True(e.Query("hit(nested).").Success);
    }

    [Fact]
    public void WrapperWithVarsSharedAcrossArgs_BindingsFlow()
    {
        // The condition binds X; the then-goal uses it — the unfolded
        // ( pick(X) -> use(X) ; true ) must see the binding.
        var e = Make(IfThen
            + "pick(7).\n"
            + "go(R) :- ifthen(pick(X), R = X).\n");
        var s = e.Query("go(R).");
        Assert.True(s.Success);
        Assert.Equal(7, s.Get<int>("R"));
    }
}
