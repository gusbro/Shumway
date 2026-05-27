using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The <c>implicit_dynamic</c> prolog_flag controls whether
/// <c>assertz/1</c> on an undefined predicate auto-promotes it to a
/// dynamic predicate (matches SWI / SICStus / GNU default behaviour)
/// or raises <c>permission_error(modify, static_procedure, _)</c>
/// (the original Shumway / ISO-strict behaviour).
///
/// <para>Default is <c>true</c> — for compatibility with programs
/// written for other Prolog systems. <c>false</c> opts back into
/// ISO-strict mode.</para>
/// </summary>
public class ImplicitDynamicFlagTests
{
    [Fact]
    public void Default_AssertzOnUndefined_AutoPromotes()
    {
        var engine = new PrologEngine();
        // No `:- dynamic pepe/0.` anywhere — would have raised
        // permission_error pre-Phase 19+ but the default
        // implicit_dynamic = true now auto-promotes.
        engine.ConsultString(
            ":- public test/0.\n"
            + "test :- assertz(pepe), call(pepe).\n");
        Assert.True(engine.Query("test.").Success);
    }

    [Fact]
    public void Default_AssertaOnUndefined_AutoPromotes()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public test/0.\n"
            + "test :- asserta(juan), call(juan).\n");
        Assert.True(engine.Query("test.").Success);
    }

    [Fact]
    public void DefaultFlag_ReportsTrue()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public probe/1.\n"
            + "probe(V) :- current_prolog_flag(implicit_dynamic, V).\n");
        var sol = engine.Query("probe(V).");
        Assert.True(sol.Success);
        Assert.Equal("true", sol.Bindings["V"].ToString());
    }

    [Fact]
    public void FlagFalse_AssertzOnUndefined_RaisesPermissionError()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- set_prolog_flag(implicit_dynamic, false).\n"
            + ":- public test/0.\n"
            + "test :- assertz(pepe), call(pepe).\n");
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(() => engine.Query("test."));
        Assert.Contains("permission_error", ex.Message);
    }

    [Fact]
    public void FlagFalse_DeclaredDynamic_StillAccepted()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- set_prolog_flag(implicit_dynamic, false).\n"
            + ":- dynamic pepe/0.\n"
            + ":- public test/0.\n"
            + "test :- assertz(pepe), call(pepe).\n");
        Assert.True(engine.Query("test.").Success);
    }

    [Fact]
    public void Default_AssertzOnStaticPredicate_RaisesPermissionError()
    {
        // Auto-promotion only fires when the predicate has no clauses.
        // A predicate with static clauses still raises permission_error
        // regardless of the flag's value — that's how SWI/SICStus/GNU
        // behave too.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public test/0.\n"
            + "existing(static_clause).\n"
            + "test :- assertz(existing(new_one)).\n");
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(() => engine.Query("test."));
        Assert.Contains("permission_error", ex.Message);
    }

    [Fact]
    public void Default_AssertzOnBuiltin_RaisesPermissionError()
    {
        // Builtins are always static.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public test/0.\n"
            + "test :- assertz(true).\n");
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(() => engine.Query("test."));
        Assert.Contains("permission_error", ex.Message);
    }

    [Fact]
    public void AutoPromote_CompoundClauseHead_Works()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public test/0.\n"
            + "test :- assertz(item(a, 1)), assertz(item(b, 2)),\n"
            + "    item(a, X), X =:= 1,\n"
            + "    item(b, Y), Y =:= 2.\n");
        Assert.True(engine.Query("test.").Success);
    }

    [Fact]
    public void AutoPromote_ThenRetract_Works()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public test/0.\n"
            + "test :- assertz(thing(x)), retract(thing(x)),\n"
            + "    ( thing(_) -> Out = leaked ; Out = ok ),\n"
            + "    Out = ok.\n");
        Assert.True(engine.Query("test.").Success);
    }

    [Fact]
    public void FlagRuntimeToggle_RuntimeComputedHead_RespectsFlag()
    {
        // The consult-time pre-scan auto-declares literal-head
        // assertz targets up front, so runtime toggling
        // implicit_dynamic=false can't retroactively block those.
        // What it DOES block is a *runtime-computed* head — one the
        // pre-scan couldn't see because the head's functor is bound
        // dynamically. With the flag off, the runtime EnsureDynamic
        // path raises permission_error.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public test/0.\n"
            + "test :- set_prolog_flag(implicit_dynamic, false),\n"
            + "    NewHead = late_bound_pred,\n"
            + "    catch(assertz(NewHead),\n"
            + "        error(permission_error(_,_,_), _),\n"
            + "        Caught = yes),\n"
            + "    Caught == yes.\n");
        Assert.True(engine.Query("test.").Success);
    }
}
