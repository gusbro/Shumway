using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 79 — <c>verify_attributes/4</c>, the Scryer/SICStus-style
/// attributed-variable unify hook. It supersedes chunk 78's SWI-style
/// <c>attr_unify_hook/3</c>: instead of the hook doing the check inline,
/// it inspects the attribute and <em>returns a list of goals</em> the
/// engine runs after the binding. Collecting goals composes better
/// across modules and is the substrate residual-constraint projection
/// will plug into.
///
/// <para>The hook is <c>verify_attributes(Module, AttrValue, Value, Goals)</c>
/// — a single global predicate dispatching on the <c>Module</c> atom
/// (Shumway's flat-namespace take on SICStus's <c>/3</c>). All modules'
/// hooks run, then every returned goal runs; a failing hook or a failing
/// goal fails the triggering unification. With no <c>verify_attributes/4</c>
/// defined the wakeups are silent no-ops, so attributed variables stay
/// exactly as hookless as the chunk-77 foundation.</para>
/// </summary>
public class Chunk79Tests
{
    private static PrologEngine WithHook(string hookClauses)
    {
        var engine = new PrologEngine();
        engine.ConsultString(hookClauses);
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
        // The hook only succeeds for one exact (module, attr, value)
        // triple, so its success pins all three arguments.
        var engine = WithHook("verify_attributes(m, the_attr, the_value, []).");
        Assert.True(engine.Query("put_attr(X, m, the_attr), X = the_value.").Success);
        Assert.False(engine.Query("put_attr(X, m, wrong_attr), X = the_value.").Success);
        Assert.False(engine.Query("put_attr(X, m, the_attr), X = wrong_value.").Success);
    }

    // ---- the returned goals ------------------------------------------

    [Fact]
    public void ReturnedGoal_RunsAndGatesTheUnification()
    {
        // verify_attributes returns [check(Value)]; the engine runs it.
        var engine = WithHook(
            "verify_attributes(m, _, V, [check(V)]).\n" +
            "check(ok).");
        Assert.True(engine.Query("put_attr(X, m, _), X = ok.").Success);
        // check(bad) has no matching clause — the returned goal fails,
        // and that fails the unification.
        Assert.False(engine.Query("put_attr(X, m, _), X = bad.").Success);
    }

    [Fact]
    public void ReturnedGoal_CanBindCallerVariables()
    {
        // The returned goal unifies the caller's Result with the
        // attribute value; that binding flows back out of the query.
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
            "verify_attributes(m, _, _, [first_goal, second_goal]).\n" +
            "first_goal.\n" +
            "second_goal.");
        Assert.True(engine.Query("put_attr(X, m, 1), X = v.").Success);
    }

    [Fact]
    public void OneReturnedGoalFailing_FailsTheUnification()
    {
        // Two goals returned; the second fails outright.
        var engine = WithHook(
            "verify_attributes(m, _, _, [first_goal, fail]).\n" +
            "first_goal.");
        Assert.False(engine.Query("put_attr(X, m, 1), X = v.").Success);
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
        // Unifying an attributed variable with a plain variable doesn't
        // bind it to a value — the attvar survives — so no hook fires,
        // even one that would fail.
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
