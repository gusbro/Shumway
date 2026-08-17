using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 80 — <c>verify_attributes</c> hooks run in the <em>live</em>
/// engine. Chunks 78–79 ran the wakeup machinery in an isolated
/// sub-engine, so a hook (and the goals it returned) could not see the
/// real attributed variables — fatal for a constraint library like
/// clpz, whose propagation goals introspect the live constraint store.
///
/// <para>Chunk 80 replaces the sub-engine runner with an in-engine
/// meta-call: at a goal boundary the interpreter builds a
/// <c>verify_attributes(Module, AttrValue, Value, Goals)</c> goal per
/// queued wakeup and runs it — and every goal it returns — against the
/// live heap, with a backtrack floor containing inner failure and a
/// once-style cut discarding any choice points left behind. The
/// <c>verify_attributes/4</c> hook must be declared <c>:- public</c> so
/// the interpreter can resolve it by its bare functor.</para>
/// </summary>
public class Chunk80Tests
{
    /// <summary>An engine whose program defines a <c>verify_attributes/4</c>
    /// hook. The <c>:- public</c> declaration is required — the
    /// interpreter resolves the hook by its bare functor id.</summary>
    private static PrologEngine WithHook(string hookClauses)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- public verify_attributes/4.\n" + hookClauses);
        return engine;
    }

    // ---- the hook gates the unification --------------------------------

    [Fact]
    public void VerifySucceeding_WithNoGoals_LetsUnificationSucceed()
    {
        var engine = WithHook("verify_attributes(m, _, _, []).");
        Assert.True(engine.Query("put_attr(X, m, 1), X = concrete.").Success);
    }

    [Fact]
    public void VerifyFailing_FailsTheUnification()
    {
        var engine = WithHook("verify_attributes(m, _, _, _) :- fail.");
        Assert.False(engine.Query("put_attr(X, m, 1), X = concrete.").Success);
    }

    [Fact]
    public void UndefinedVerify_UnifiesHooklessly()
    {
        // No verify_attributes/4 — the chunk-77 foundation: the
        // attributed variable just binds, no hook consulted.
        var engine = new PrologEngine();
        Assert.True(engine.Query("put_attr(X, m, 1), X = concrete.").Success);
    }

    [Fact]
    public void Verify_ReceivesModuleAttributeValueAndBoundValue()
    {
        var engine = WithHook("verify_attributes(m, the_attr, the_value, []).");
        Assert.True(engine.Query("put_attr(X, m, the_attr), X = the_value.").Success);
        Assert.False(engine.Query("put_attr(X, m, wrong_attr), X = the_value.").Success);
        Assert.False(engine.Query("put_attr(X, m, the_attr), X = wrong_value.").Success);
    }

    // ---- the returned goals -------------------------------------------

    [Fact]
    public void ReturnedGoal_RunsAndGatesTheUnification()
    {
        // A predicate named directly in a returned goal must be public —
        // the interpreter resolves it by its bare functor.
        var engine = WithHook(
            ":- public check/1.\n" +
            "verify_attributes(m, _, V, [check(V)]).\n" +
            "check(ok).");
        Assert.True(engine.Query("put_attr(X, m, _), X = ok.").Success);
        Assert.False(engine.Query("put_attr(X, m, _), X = bad.").Success);
    }

    [Fact]
    public void ReturnedGoal_CanBindCallerVariables()
    {
        var engine = WithHook(
            "verify_attributes(m, AttrVal, wrapped(R), [R = AttrVal]).");
        var sol = engine.Query("put_attr(X, m, payload), X = wrapped(Result).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("payload"), sol["Result"]);
    }

    [Fact]
    public void MultipleReturnedGoals_AllRun()
    {
        var engine = WithHook(
            ":- public first_goal/0.\n" +
            ":- public second_goal/0.\n" +
            "verify_attributes(m, _, _, [first_goal, second_goal]).\n" +
            "first_goal.\n" +
            "second_goal.");
        Assert.True(engine.Query("put_attr(X, m, 1), X = v.").Success);
    }

    [Fact]
    public void OneReturnedGoalFailing_FailsTheUnification()
    {
        var engine = WithHook(
            ":- public first_goal/0.\n" +
            "verify_attributes(m, _, _, [first_goal, fail]).\n" +
            "first_goal.");
        Assert.False(engine.Query("put_attr(X, m, 1), X = v.").Success);
    }

    [Fact]
    public void ReturnedGoal_RunsInTheLiveEngine_SoSideEffectsPersist()
    {
        // The defining proof of chunk 80: a returned goal runs in the
        // live engine, not an isolated sub-engine, so an assertz/1 it
        // performs is still visible once the query completes. Under the
        // chunks 78-79 sub-engine runner this assertion would be lost.
        var engine = WithHook(
            ":- dynamic marker/0.\n" +
            "verify_attributes(m, _, _, [assertz(marker)]).");
        Assert.True(engine.Query("put_attr(X, m, 1), X = v.").Success);
        Assert.True(engine.Query("marker.").Success);
    }

    // ---- multiple modules ---------------------------------------------

    [Fact]
    public void EveryModuleHookRuns_OnUnification()
    {
        var engine = WithHook(
            "verify_attributes(a, _, _, []).\n" +
            "verify_attributes(b, _, _, []).");
        Assert.True(engine.Query(
            "put_attr(X, a, 1), put_attr(X, b, 2), X = v.").Success);
    }

    [Fact]
    public void AnyModuleHookFailing_FailsTheUnification()
    {
        var engine = WithHook(
            "verify_attributes(a, _, _, []).\n" +
            "verify_attributes(b, _, _, _) :- fail.");
        Assert.False(engine.Query(
            "put_attr(X, a, 1), put_attr(X, b, 2), X = v.").Success);
    }

    // ---- head unification, not just =/2 --------------------------------

    [Fact]
    public void Verify_FiresOnClauseHeadUnification()
    {
        var engine = WithHook(
            "take(a).\n" +
            "verify_attributes(m, _, V, []) :- V == a.");
        Assert.True(engine.Query("put_attr(X, m, _), take(X).").Success);
    }

    [Fact]
    public void Verify_FailingOnHeadUnification_FailsTheCall()
    {
        var engine = WithHook(
            "take(b).\n" +
            "verify_attributes(m, _, V, []) :- V == a.");
        Assert.False(engine.Query("put_attr(X, m, _), take(X).").Success);
    }

    // ---- attvar + plain var / attvar + attvar --------------------------

    [Fact]
    public void AttvarBoundToPlainVariable_FiresNoHook()
    {
        var engine = WithHook("verify_attributes(m, _, _, _) :- fail.");
        Assert.True(engine.Query("put_attr(X, m, 1), X = Y, var(Y).").Success);
    }

    [Fact]
    public void TwoAttvars_HookFiresWithTheOtherVariable()
    {
        var engine = WithHook("verify_attributes(m, _, Other, []) :- var(Other).");
        Assert.True(engine.Query(
            "put_attr(X, m, 1), put_attr(Y, m, 1), X = Y.").Success);
    }

    [Fact]
    public void TwoAttvars_HookCanRejectTheUnification()
    {
        var engine = WithHook("verify_attributes(m, _, Other, []) :- nonvar(Other).");
        Assert.False(engine.Query(
            "put_attr(X, m, 1), put_attr(Y, m, 1), X = Y.").Success);
    }

    // ---- backtracking --------------------------------------------------

    [Fact]
    public void FailedHook_BacktracksCleanlyToAnAlternative()
    {
        var engine = WithHook("verify_attributes(m, _, V, []) :- V == good.");
        Assert.True(engine.Query(
            "put_attr(X, m, _), ( X = bad ; X = good ).").Success);
    }

    [Fact]
    public void DynamicPredicate_NamedByAReturnedGoal_ResolvesWithoutPublic()
    {
        // The hook itself is :- public; the predicate it *names in a
        // returned goal* (check/1) is :- dynamic but not :- public.
        // Dynamic predicates are never module-mangled, so the in-engine
        // meta-call still resolves check/1 by its bare functor.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public verify_attributes/4.
            :- dynamic check/1.
            verify_attributes(m, _, V, [check(V)]).
            check(ok).
            """);
        Assert.True(engine.Query("put_attr(X, m, _), X = ok.").Success);
        Assert.False(engine.Query("put_attr(X, m, _), X = bad.").Success);
    }

    // ---- domain-constraint worked example ------------------------------

    [Fact]
    public void DomainConstraint_GatesValuesAgainstTheDomain()
    {
        var engine = WithHook(
            "verify_attributes(dom, List, Val, []) :- " +
            "( var(Val) -> true ; member(Val, List) ).");
        Assert.True(engine.Query("put_attr(X, dom, [1,2,3]), X = 2.").Success);
        Assert.False(engine.Query("put_attr(X, dom, [1,2,3]), X = 9.").Success);
    }
}
