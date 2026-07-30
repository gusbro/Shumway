using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-040 Component 3 — per-module attribute-unification hook. An
/// export-qualified module's own <c>Module$verify_attributes/4</c> is resolved
/// and dispatched per attribute module (<see cref="Shumway.Core.Activation.
/// Verify4FunctorId"/>), so two dialects' constraint libraries each own their
/// hook and coexist — no bare-global <c>:- public verify_attributes/4</c>
/// collision. A single variable carrying attributes from two modules runs both
/// hooks.</summary>
public sealed class PerModuleAttributeHookTests
{
    // An export-qualified module whose module-local verify_attributes/4 enforces
    // "the value must be =< the ceiling stored in the le(Max) attribute".
    private const string ModA = """
        :- module(ma, [post_a/2]).
        post_a(V, Max) :- put_attr(V, ma, le(Max)).
        verify_attributes(ma, le(Max), Value, Goals) :-
            ( integer(Value) -> Value =< Max ; true ), Goals = [].
        """;

    // A second module whose hook enforces ">= the floor stored in ge(Min)".
    private const string ModB = """
        :- module(mb, [post_b/2]).
        post_b(V, Min) :- put_attr(V, mb, ge(Min)).
        verify_attributes(mb, ge(Min), Value, Goals) :-
            ( integer(Value) -> Value >= Min ; true ), Goals = [].
        """;

    [Fact]
    public void ModuleLocalHook_DispatchesPerModule()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        // The module-local ma$verify_attributes/4 fires when the attributed
        // variable is bound: 3 =< 5 holds, 9 =< 5 does not.
        Assert.True(e.Query("ma:post_a(X, 5), X = 3.").Success);
        Assert.False(e.Query("ma:post_a(X, 5), X = 9.").Success);
    }

    [Fact]
    public void TwoModuleLocalHooks_CoexistOnOneEngine()
    {
        // Both modules define a module-local verify_attributes/4. With the old
        // bare-global :- public model this tripped ValidatePublicUniqueness; now
        // each owns ma$… / mb$… and they load together.
        var e = new PrologEngine();
        e.ConsultString(ModA);
        e.ConsultString(ModB);
        Assert.True(e.Query("ma:post_a(X, 5), X = 4.").Success);
        Assert.True(e.Query("mb:post_b(Y, 2), Y = 4.").Success);
        Assert.False(e.Query("mb:post_b(Y, 2), Y = 1.").Success);   // 1 >= 2 fails
    }

    [Fact]
    public void BakedClpfd_CoexistsWith_AModuleLocalHook()
    {
        // The baked clpfd (bare-global multifile /4) and a user module's
        // module-local /4 hook run side by side: an FD variable constrained by
        // clpfd AND carrying a module-local ceiling, both enforced.
        var e = new PrologEngine();
        e.UseClpfd();
        e.ConsultString(ModA);
        Assert.True(e.Query("X in 1..9, ma:post_a(X, 5), indomain(X), X =< 5.").Success);
    }

    [Fact]
    public void OneVariable_TwoModules_BothHooksRun()
    {
        // A single variable carries an attribute from each module; binding it
        // must satisfy BOTH hooks (SICStus/SWI semantics: every module's hook
        // runs). le(6) ∧ ge(2): 4 passes both, 9 fails le, 1 fails ge.
        var e = new PrologEngine();
        e.ConsultString(ModA);
        e.ConsultString(ModB);
        Assert.True(e.Query("ma:post_a(X, 6), mb:post_b(X, 2), X = 4.").Success);
        Assert.False(e.Query("ma:post_a(X, 6), mb:post_b(X, 2), X = 9.").Success);
        Assert.False(e.Query("ma:post_a(X, 6), mb:post_b(X, 2), X = 1.").Success);
    }
}
