using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Tier-1 SWI-library gap predicates implemented as first-class engine
/// features: <c>flag/3</c> / <c>set_flag/2</c> / <c>get_flag/2</c> (a global,
/// non-backtrackable read-modify-write counter store) and
/// <c>setup_call_cleanup/3</c> / <c>call_cleanup/2</c>.</summary>
public sealed class FlagAndCleanupTests
{
    // ---------- flag/3 ----------

    [Fact]
    public void Flag_DefaultsToZero()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("flag(fresh_key, Old, Old), Old == 0.").Success);
        Assert.True(e.Query("get_flag(never_set, V), V == 0.").Success);
    }

    [Fact]
    public void Flag_ReadModifyWrite_EvaluatesExpression()
    {
        var e = new PrologEngine();
        // flag(K, X, X+1) reads the old value into X, then stores X+1.
        Assert.True(e.Query("flag(c, X0, X0+1), X0 == 0.").Success);   // fresh: old 0, now 1
        Assert.True(e.Query("flag(c, X1, X1+1), X1 == 1.").Success);   // persisted: old 1, now 2
        Assert.True(e.Query("get_flag(c, V), V == 2.").Success);
    }

    [Fact]
    public void Flag_PersistsAcrossFailureDrivenLoop()
    {
        var e = new PrologEngine();
        // The gensym idiom: a flag counter is NOT backtracked, so it survives a
        // failure-driven loop and ends at the number of iterations.
        e.Query("( between(1, 5, _), flag(counter, Old, Old+1), fail ; true ).");
        Assert.True(e.Query("get_flag(counter, V), V == 5.").Success);
    }

    [Fact]
    public void SetFlag_OverwritesAndAtomValues()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("set_flag(mode, verbose), get_flag(mode, V), V == verbose.").Success);
        Assert.True(e.Query("set_flag(n, 41), flag(n, Old, Old+1), Old == 41, get_flag(n, W), W == 42.").Success);
    }

    [Fact]
    public void Flag_CompoundKey_Works()
    {
        var e = new PrologEngine();
        // SWI's library(gensym) keys on a compound gensym(Base). A compound key
        // is distinct from a same-named atom and from a different compound.
        Assert.True(e.Query("flag(gensym(foo), O, O+1), O == 0, get_flag(gensym(foo), V), V == 1.").Success);
        Assert.True(e.Query("get_flag(gensym(bar), V), V == 0.").Success);   // distinct key
        Assert.True(e.Query("flag(gensym(foo), O, O+1), O == 1.").Success);   // persisted
    }

    [Fact]
    public void Flag_UnboundKeyIsInstantiationError()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("catch(flag(_, _, 1), error(instantiation_error, _), true).").Success);
    }

    // ---------- setup_call_cleanup/3 ----------

    [Fact]
    public void Cleanup_RunsOnDeterministicSuccess()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(log/1).");
        Assert.True(e.Query(
            "setup_call_cleanup(assertz(log(setup)), true, assertz(log(cleaned))).").Success);
        // Both setup and cleanup ran, cleanup exactly once.
        Assert.True(e.Query("log(setup).").Success);
        Assert.Single(e.QueryAll("log(cleaned)."));
    }

    [Fact]
    public void Cleanup_BindingsReachTheCaller()
    {
        // SWI's determinism-detection idiom: setup_call_cleanup(true, G, Det=true)
        // — the cleanup goal shares variables with the caller, so its binding must
        // survive into the continuation. Regression: the cleanup used to run the
        // assertz-retract COPY of the goal (renamed variables), so Det stayed
        // unbound outside.
        var e = new PrologEngine();
        Assert.Equal("yes", e.QueryFirst<string>(
            "setup_call_cleanup(true, true, Det = yes), Ret = Det.", "Ret"));
    }

    [Fact]
    public void Cleanup_DeterministicCall_LeavesNoChoicePoint()
    {
        var e = new PrologEngine();
        // A deterministic Goal must keep setup_call_cleanup deterministic
        // (exactly one solution, no spurious backtrack into the cleanup fallback).
        Assert.Single(e.QueryAll("setup_call_cleanup(true, X = 1, true)."));
    }

    [Fact]
    public void Cleanup_RunsOnFailure()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(mark/1).");
        Assert.False(e.Query("setup_call_cleanup(true, fail, assertz(mark(f))).").Success);
        Assert.Single(e.QueryAll("mark(f)."));   // cleanup ran despite Goal failing
    }

    [Fact]
    public void Cleanup_RunsOnError_ThenReRaises()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(mark/1).");
        Assert.True(e.Query(
            "catch(setup_call_cleanup(true, throw(boom), assertz(mark(e))), boom, true).").Success);
        Assert.Single(e.QueryAll("mark(e)."));   // cleanup ran, then boom re-raised
    }

    [Fact]
    public void Cleanup_RunsWhenCallerCutsNondetGoal()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(closed/0).");
        // Nondet goal, caller commits to the first solution with a cut: the
        // engine fires the cleanup when the leftover choice points are pruned.
        Assert.True(e.Query(
            "setup_call_cleanup(true, member(X,[1,2,3]), assertz(closed)), X == 1, !.").Success);
        Assert.Single(e.QueryAll("closed."));
    }

    [Fact]
    public void Cleanup_RunsWhenOnceCommitsNondetGoal()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(oc/0).");
        // once/1 around a nondet setup_call_cleanup: the once-cut fires cleanup.
        Assert.True(e.Query(
            "once(setup_call_cleanup(true, member(_,[1,2,3]), assertz(oc))).").Success);
        Assert.Single(e.QueryAll("oc."));
    }

    [Fact]
    public void Cleanup_NondetGoal_IsTransparent_CleanupAfterExhaustion()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic(done/1).
            p(1).
            p(2).
            p(3).
            """);
        // Goal is nondeterministic: setup_call_cleanup must yield all three
        // solutions (transparency), and Cleanup runs exactly once.
        var sols = e.QueryAll("setup_call_cleanup(true, p(X), assertz(done(yes))).")
                    .Select(s => s.Get<int>("X")).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, sols);
        Assert.Single(e.QueryAll("done(yes)."));
    }

    // ---------- gensym/2 (built on flag/3) ----------

    [Fact]
    public void Gensym_GeneratesSequentialAtoms()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("gensym(foo, X), X == foo1.").Success);
        Assert.True(e.Query("gensym(foo, X), X == foo2.").Success);
        // A distinct base has its own counter.
        Assert.True(e.Query("gensym(bar, X), X == bar1.").Success);
    }

    [Fact]
    public void Gensym_CounterSurvivesFailureDrivenLoop()
    {
        var e = new PrologEngine();
        e.Query("( between(1, 3, _), gensym(g, _), fail ; true ).");
        Assert.True(e.Query("gensym(g, X), X == g4.").Success);   // 3 in the loop, then 4
    }

    [Fact]
    public void ResetGensym_RestartsCounter()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("gensym(h, h1), gensym(h, h2).").Success);
        Assert.True(e.Query("reset_gensym(h), gensym(h, X), X == h1.").Success);
    }

    [Fact]
    public void ResetGensym_All()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("gensym(a, a1), gensym(b, b1).").Success);
        Assert.True(e.Query("reset_gensym, gensym(a, X), gensym(b, Y), X == a1, Y == b1.").Success);
    }

    [Fact]
    public void Gensym_NonAtomBaseIsError()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("catch(gensym(123, _), error(type_error(atom, 123), _), true).").Success);
    }

    [Fact]
    public void Cleanup_RunsOnExceptionFromBelow_CaughtByOuter()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(closed/0).");
        // A nondet Goal succeeds leaving choice points, then an exception is
        // thrown AFTER it and caught by an outer catch — unwinding past the
        // leftover scope must fire cleanup.
        Assert.True(e.Query(
            "catch((setup_call_cleanup(true, member(_,[1,2,3]), assertz(closed)), throw(boom)), boom, true).").Success);
        Assert.Single(e.QueryAll("closed."));
    }

    [Fact]
    public void Cleanup_RunsOnExceptionFromDeeperPredicate()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic(cl/0).
            boom_below :- throw(deep).
            p :- setup_call_cleanup(true, member(_,[1,2,3]), assertz(cl)), boom_below.
            """);
        Assert.True(e.Query("catch(p, deep, true).").Success);
        Assert.Single(e.QueryAll("cl."));
    }

    [Fact]
    public void Cleanup_RunsOnUncaughtException()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(cl/0).");
        // The exception is uncaught (propagates out of the query); cleanup still
        // fires during teardown and the ball still propagates.
        var ex = Assert.ThrowsAny<System.Exception>(() =>
            e.Query("setup_call_cleanup(true, member(_,[1,2,3]), assertz(cl)), throw(boom)."));
        Assert.Contains("boom", ex.Message);
        Assert.Single(e.QueryAll("cl."));
    }

    [Fact]
    public void Cleanup_RunsWhenCallerStopsAtFirstSolution()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(closed/0).");
        // The caller takes the FIRST solution and stops (no cut) — the leftover
        // choice points are abandoned at query teardown, firing cleanup (the SWI
        // toplevel-cancel case).
        Assert.True(e.Query(
            "setup_call_cleanup(true, member(X,[1,2,3]), assertz(closed)), X == 1.").Success);
        Assert.Single(e.QueryAll("closed."));
    }

    [Fact]
    public void Cleanup_HasOnceSemantics_ChoicePointsDestroyed()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(k/1).");
        // A non-deterministic Cleanup runs with once/1 semantics: only its first
        // solution, choice points destroyed.
        e.Query("setup_call_cleanup(true, true, (member(V,[a,b,c]), assertz(k(V)))).");
        Assert.Single(e.QueryAll("k(_)."));
    }

    [Fact]
    public void Cleanup_ExceptionPropagates()
    {
        var e = new PrologEngine();
        // An exception raised by Cleanup propagates (SWI: not swallowed by the
        // once/ignore wrapper).
        Assert.True(e.Query(
            "catch(setup_call_cleanup(true, true, throw(cleanerr)), cleanerr, true).").Success);
    }

    [Fact]
    public void CallCleanup_IsSetupCallCleanupWithoutSetup()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(cc/1).");
        // Deterministic goal: cleanup runs immediately on success.
        Assert.True(e.Query("call_cleanup(memberchk(2, [1,2,3]), assertz(cc(ok))).").Success);
        Assert.Single(e.QueryAll("cc(ok)."));
    }

    [Fact]
    public void Cleanup_NondetGoal_RunsOnceAfterFullEnumeration()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(cc2/1).");
        // A non-deterministic goal whose choice points are fully backtracked:
        // one solution, cleanup exactly once (on exhaustion). (Cleanup on an
        // ABANDONED first solution is the documented limitation.)
        int n = e.QueryAll("call_cleanup(member(2, [1,2,3]), assertz(cc2(ok))).").Count();
        Assert.Equal(1, n);
        Assert.Single(e.QueryAll("cc2(ok)."));
    }
}
