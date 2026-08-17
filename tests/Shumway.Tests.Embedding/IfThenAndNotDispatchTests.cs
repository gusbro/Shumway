using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Two control-flow fixes that surfaced compiling Blint.pl:
///
/// <list type="bullet">
/// <item>Standalone <c>(A -> B)</c> as a body goal (without a
/// trailing <c>; C</c> else branch). MetaTransform now rewrites
/// it to <c>(A -> B ; fail)</c> per ISO §7.8.7 so the existing
/// disjunction synthesis fires. Without this the WAM compiler
/// emitted a call to <c>-&gt;/2</c> as a plain procedure, which
/// raised <c>existence_error/2</c> at runtime.</item>
///
/// <item><c>not/1</c> in a runtime meta-call goal. The bytecode
/// interpreter's <c>DispatchCall</c> recognised <c>\+/1</c> as a
/// negation-helper alias but not <c>not/1</c> — programs that
/// used <c>not(G)</c> via <c>call/1</c> raised
/// <c>existence_error/2</c> on <c>not/1</c>. The dispatch now
/// routes both to <c>$call_neg</c>.</item>
/// </list>
/// </summary>
public class IfThenAndNotDispatchTests
{
    [Fact]
    public void StandaloneIfThen_InClauseBody_Succeeds()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- public test/0.
            test :- true -> true.
            """);
        Assert.True(e.Query("test.").Success);
    }

    [Fact]
    public void StandaloneIfThen_ConditionFails_GoalFails()
    {
        // ISO §7.8.7: `(A -> B)` is `(A -> B ; fail)` — when A fails,
        // the whole thing fails (does NOT fall through to a sibling
        // conjunct).
        var e = new PrologEngine();
        e.ConsultString("""
            :- public test/0.
            test :- (fail -> true).
            """);
        Assert.False(e.Query("test.").Success);
    }

    [Fact]
    public void IfThenWithMetaVariables_BlintPattern()
    {
        // The Blint.pl shape: ifthen(X, Y) :- X -> !, Y.
        var e = new PrologEngine();
        e.ConsultString("""
            :- public ifthen/2.
            ifthen(X, Y) :- X -> !, Y.
            ifthen(_, _) :- !.
            """);
        var sol = e.Query("ifthen(true, X = ok).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void IfThenElse_StillWorks_Regression()
    {
        // The (A -> B ; C) form must still work the same way — the
        // new standalone rewrite must not destabilise it.
        var e = new PrologEngine();
        e.ConsultString("""
            :- public p/1.
            p(X) :- (X = ok -> Y = yes ; Y = no), write(Y).
            """);
        Assert.True(e.Query("p(ok).").Success);
    }

    [Fact]
    public void NotMetaCall_Succeeds()
    {
        // call(not(fail)) — runtime meta-call of not/1.
        var e = new PrologEngine();
        Assert.True(e.Query("call(not(fail)).").Success);
    }

    [Fact]
    public void NotMetaCall_NegatesSuccess()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("call(not(true)).").Success);
    }

    [Fact]
    public void NotMetaCall_ViaConstructedGoal_BlintPattern()
    {
        // Approximates Blint.pl's ifthen(not(show_in_console), ...).
        // The variable carries `not(G)` to call/1 inside a body.
        var e = new PrologEngine();
        e.ConsultString("""
            :- public test/0.
            guarded(X, Y) :- X -> Y.
            show_in_console :- fail.
            test :- guarded(not(show_in_console), true).
            """);
        Assert.True(e.Query("test.").Success);
    }

    [Fact]
    public void Backslash_PlusOne_MetaCall_StillWorks()
    {
        // \+/1 was already routed; regression guard that the alias
        // addition didn't break it.
        var e = new PrologEngine();
        Assert.True(e.Query("call(\\+ fail).").Success);
        Assert.False(e.Query("call(\\+ true).").Success);
    }
}
