using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 78 — <c>attr_unify_hook</c> wakeups (Phase 4). Chunk 77 left
/// attributed variables hookless: unifying one just bound it. Chunk 78
/// adds the wakeup mechanism — when an attributed variable is unified
/// with a term, the engine queues a hook call per attribute module and
/// runs them at the next goal boundary.
///
/// <para>The hook is the user predicate
/// <c>attr_unify_hook(Module, AttributeValue, OtherTerm)</c> — a single
/// global predicate that dispatches on the <c>Module</c> atom (Shumway's
/// flat-namespace take on SWI's <c>Module:attr_unify_hook/2</c>). A hook
/// that fails fails the triggering unification. When no
/// <c>attr_unify_hook/3</c> is defined the wakeups are silent no-ops, so
/// attributed variables stay exactly as hookless as chunk 77.</para>
/// </summary>
public class Chunk78Tests
{
    private static PrologEngine WithHook(string hookClauses)
    {
        var engine = new PrologEngine();
        engine.ConsultString(hookClauses);
        return engine;
    }

    // ---- the hook fires and gates the unification ----------------------

    [Fact]
    public void HookSucceeds_UnificationSucceeds()
    {
        var engine = WithHook("attr_unify_hook(m, _, _).");
        Assert.True(engine.Query("put_attr(X, m, 1), X = concrete.").Success);
    }

    [Fact]
    public void HookFails_UnificationFails()
    {
        var engine = WithHook("attr_unify_hook(m, _, _) :- fail.");
        Assert.False(engine.Query("put_attr(X, m, 1), X = concrete.").Success);
    }

    [Fact]
    public void UndefinedHook_UnifiesHooklessly()
    {
        // No attr_unify_hook/3 anywhere — the chunk-77 foundation: the
        // attributed variable just binds, no hook consulted.
        var engine = new PrologEngine();
        Assert.True(engine.Query("put_attr(X, m, 1), X = concrete.").Success);
    }

    // ---- the hook sees the right arguments -----------------------------

    [Fact]
    public void Hook_ReceivesModuleAttributeValueAndBoundTerm()
    {
        // The hook only succeeds for one exact (module, attr, other)
        // triple, so its success pins all three arguments.
        var engine = WithHook("attr_unify_hook(m, the_attr, the_value).");
        Assert.True(engine.Query("put_attr(X, m, the_attr), X = the_value.").Success);
        Assert.False(engine.Query("put_attr(X, m, wrong_attr), X = the_value.").Success);
        Assert.False(engine.Query("put_attr(X, m, the_attr), X = wrong_value.").Success);
    }

    [Fact]
    public void Hook_CanBindVariablesInTheBoundTerm()
    {
        // X is unified with wrapped(Result); the hook unifies Result with
        // the attribute value, and that binding flows back to the caller.
        var engine = WithHook(
            "attr_unify_hook(m, AttrVal, wrapped(W)) :- W = AttrVal.");
        var sol = engine.Query("put_attr(X, m, payload), X = wrapped(Result).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("payload"), sol["Result"]);
    }

    // ---- domain-constraint example (the /3 design's worked example) ----

    [Fact]
    public void DomainConstraint_AcceptsAValueInsideTheDomain()
    {
        var engine = WithHook(
            "attr_unify_hook(dom, List, Val) :- ( var(Val) -> true ; member(Val, List) ).");
        Assert.True(engine.Query("put_attr(X, dom, [1,2,3]), X = 2.").Success);
    }

    [Fact]
    public void DomainConstraint_RejectsAValueOutsideTheDomain()
    {
        var engine = WithHook(
            "attr_unify_hook(dom, List, Val) :- ( var(Val) -> true ; member(Val, List) ).");
        Assert.False(engine.Query("put_attr(X, dom, [1,2,3]), X = 9.").Success);
    }

    // ---- multiple modules ---------------------------------------------

    [Fact]
    public void EveryModuleHookFires_OnUnification()
    {
        var engine = WithHook(
            "attr_unify_hook(a, _, _).\n" +
            "attr_unify_hook(b, _, _).");
        Assert.True(engine.Query(
            "put_attr(X, a, 1), put_attr(X, b, 2), X = v.").Success);
    }

    [Fact]
    public void AnyModuleHookFailing_FailsTheUnification()
    {
        // module a's hook passes, module b's hook fails — unification fails.
        var engine = WithHook(
            "attr_unify_hook(a, _, _).\n" +
            "attr_unify_hook(b, _, _) :- fail.");
        Assert.False(engine.Query(
            "put_attr(X, a, 1), put_attr(X, b, 2), X = v.").Success);
    }

    // ---- head unification, not just =/2 --------------------------------

    [Fact]
    public void Hook_FiresOnClauseHeadUnification()
    {
        // Calling take(X) with X attributed unifies X against the clause
        // head's `a` — the hook must fire for that head match too.
        var engine = WithHook(
            "take(a).\n" +
            "attr_unify_hook(m, _, V) :- V == a.");
        Assert.True(engine.Query("put_attr(X, m, _), take(X).").Success);
    }

    [Fact]
    public void Hook_FailingOnHeadUnification_FailsTheCall()
    {
        var engine = WithHook(
            "take(b).\n" +
            "attr_unify_hook(m, _, V) :- V == a.");
        // Head unifies X with b; the hook demands `a`, so take(X) fails.
        Assert.False(engine.Query("put_attr(X, m, _), take(X).").Success);
    }

    // ---- backtracking --------------------------------------------------

    [Fact]
    public void FailedHook_BacktracksCleanlyToAnAlternative()
    {
        var engine = WithHook("attr_unify_hook(m, _, V) :- V == good.");
        // X = bad trips the hook and fails; backtracking reaches X = good.
        Assert.True(engine.Query(
            "put_attr(X, m, _), ( X = bad ; X = good ).").Success);
    }

    // ---- attvar + attvar -----------------------------------------------

    [Fact]
    public void AttvarBoundToPlainVariable_FiresNoHook()
    {
        // Unifying an attributed variable with a *plain* variable doesn't
        // bind it to a value — the attvar survives — so no hook fires,
        // even one that would fail.
        var engine = WithHook("attr_unify_hook(m, _, _) :- fail.");
        Assert.True(engine.Query("put_attr(X, m, 1), X = Y, var(Y).").Success);
    }

    [Fact]
    public void TwoAttvars_HookFiresWithTheOtherVariable()
    {
        var engine = WithHook("attr_unify_hook(m, _, Other) :- var(Other).");
        Assert.True(engine.Query(
            "put_attr(X, m, 1), put_attr(Y, m, 1), X = Y.").Success);
    }

    [Fact]
    public void TwoAttvars_HookCanRejectTheUnification()
    {
        var engine = WithHook("attr_unify_hook(m, _, Other) :- nonvar(Other).");
        // The other side is still a variable, so the nonvar/1 hook fails.
        Assert.False(engine.Query(
            "put_attr(X, m, 1), put_attr(Y, m, 1), X = Y.").Success);
    }
}
