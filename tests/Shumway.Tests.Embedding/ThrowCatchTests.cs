using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Coverage for chunk 23: the <c>throw/1</c> + <c>catch/3</c> exception
/// machinery, ISO-style error term construction, and the rollback /
/// rethrow behaviour when a catcher doesn't match.
/// </summary>
public class ThrowCatchTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Compound(string f, params Term[] args) => new CompoundTerm(f, args);

    // ---------- throw/1 ----------

    [Fact]
    public void Throw_Uncaught_PropagatesAsCSharpException()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => engine.Query("throw(my_error)."));
        Assert.Equal(Atom("my_error"), ex.Term);
    }

    [Fact]
    public void Throw_CompoundTerm_PreservesStructure()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => engine.Query("throw(error(type_error(integer, foo), _))."));
        var ct = Assert.IsType<CompoundTerm>(ex.Term);
        Assert.Equal("error", ct.Functor);
        Assert.Equal(2, ct.Args.Length);
        var inner = Assert.IsType<CompoundTerm>(ct.Args[0]);
        Assert.Equal("type_error", inner.Functor);
    }

    // ---------- catch/3 — success / no-throw ----------

    [Fact]
    public void Catch_GoalSucceeds_BindingsFlowBack()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            colour(red).
            colour(green).
            """);
        var sol = engine.Query("catch(colour(X), _, fail).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("red"), sol["X"]);
    }

    [Fact]
    public void Catch_GoalFails_CatchFails()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(1).");
        Assert.False(engine.Query("catch(p(2), _, true).").Success);
    }

    // ---------- catch/3 — catcher matches ----------

    [Fact]
    public void Catch_ThrowMatching_RunsRecovery()
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(throw(boom), boom, X = recovered).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("recovered"), sol["X"]);
    }

    [Fact]
    public void Catch_ThrownTerm_AvailableInCatcherPattern()
    {
        // catch(throw(my_error(42)), my_error(N), X = N) — N binds to 42,
        // then recovery's `X = N` binds X to 42.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(throw(my_error(42)), my_error(N), X = N).");
        Assert.True(sol.Success);
        Assert.Equal(Int(42), sol["X"]);
    }

    [Fact]
    public void Catch_RecoverySeesCatcherBindings()
    {
        // The recovery body references a variable that's bound during the
        // catcher unification — the value must propagate.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(throw(packet(payload_value)), packet(P), Result = got(P)).");
        Assert.True(sol.Success);
        Assert.Equal(Compound("got", Atom("payload_value")), sol["Result"]);
    }

    // ---------- catch/3 — catcher doesn't match ----------

    [Fact]
    public void Catch_ThrownDoesNotMatchCatcher_RethrowsOriginal()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => engine.Query("catch(throw(actual_error), expected_error, true)."));
        // The thrown term that surfaces is the original — not a transformed
        // version. The catcher mismatch shouldn't lose information.
        Assert.Equal(Atom("actual_error"), ex.Term);
    }

    [Fact]
    public void Catch_NestedCatch_InnerCatches()
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(catch(throw(inner), inner, X = caught_inner), _, X = caught_outer).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("caught_inner"), sol["X"]);
    }

    [Fact]
    public void Catch_NestedCatch_InnerMisses_OuterCatches()
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(catch(throw(outer), inner, X = inner_path), outer, X = outer_path).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("outer_path"), sol["X"]);
    }

    // ---------- Throw inside user clause ----------

    [Fact]
    public void Catch_ThrowFromUserPredicate_Catches()
    {
        var engine = new PrologEngine();
        engine.ConsultString("blowup :- throw(predicate_blew_up).");
        var sol = engine.Query("catch(blowup, E, X = E).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("predicate_blew_up"), sol["X"]);
    }

    // ---------- IsoError helpers ----------

    [Fact]
    public void IsoError_TypeError_BuildsCorrectTerm()
    {
        var err = IsoError.TypeError("integer", new AtomTerm("foo"));
        var ct = Assert.IsType<CompoundTerm>(err);
        Assert.Equal("error", ct.Functor);
        Assert.Equal(2, ct.Args.Length);
        var inner = Assert.IsType<CompoundTerm>(ct.Args[0]);
        Assert.Equal("type_error", inner.Functor);
        Assert.Equal(Atom("integer"), inner.Args[0]);
        Assert.Equal(Atom("foo"), inner.Args[1]);
    }

    [Fact]
    public void IsoError_InstantiationError_BuildsCorrectTerm()
    {
        var err = IsoError.InstantiationError();
        var ct = Assert.IsType<CompoundTerm>(err);
        Assert.Equal("error", ct.Functor);
        Assert.Equal(Atom("instantiation_error"), ct.Args[0]);
    }

    [Fact]
    public void IsoError_ExistenceError_BuildsCorrectTerm()
    {
        var err = IsoError.ExistenceError(
            "procedure",
            new CompoundTerm("/", new Term[] { Atom("foo"), Int(3) }));
        var ct = Assert.IsType<CompoundTerm>(err);
        var inner = Assert.IsType<CompoundTerm>(ct.Args[0]);
        Assert.Equal("existence_error", inner.Functor);
        Assert.Equal(Atom("procedure"), inner.Args[0]);
    }

    // ---------- Throw and catch with IsoError-style payloads ----------

    [Fact]
    public void Catch_IsoStyleError_PatternMatches()
    {
        // User code emits an ISO-shaped error term; the catcher destructures
        // it idiomatically.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(throw(error(type_error(integer, foo), ctx)), "
            + "error(type_error(T, V), _), Out = mismatch(T, V)).");
        Assert.True(sol.Success);
        Assert.Equal(
            Compound("mismatch", Atom("integer"), Atom("foo")),
            sol["Out"]);
    }
}
