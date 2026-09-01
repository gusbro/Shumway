using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Coverage for chunk 24: <c>clause/2</c>, <c>current_predicate/1</c>,
/// <c>abolish/1</c> — the introspection family that lets user code reflect
/// on the engine's loaded predicates.
/// </summary>
public class IntrospectionTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Compound(string f, params Term[] args) => new CompoundTerm(f, args);

    // ---------- clause/2 ----------

    [Fact]
    public void Clause_StaticFact_RaisesPermissionError()
    {
        // ISO §8.8.1.3: clause/2 reads PUBLIC (dynamic) procedures only; a
        // static user predicate is private (GNU and Scryer agree). SWI-dialect
        // modules keep SWI's introspection of their own static clauses.
        var engine = new PrologEngine();
        engine.ConsultString("greeting(hello).");
        var sol = engine.Query(
            "catch(clause(greeting(_), _), error(permission_error(A, K, P), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("access"), sol["A"]);
        Assert.Equal(Atom("private_procedure"), sol["K"]);
        var pi = Assert.IsType<CompoundTerm>(sol["P"]);
        Assert.Equal("/", pi.Functor);
    }

    [Fact]
    public void Clause_DynamicRule_BindsBodyTerm()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic double/2.\ndouble(X, Y) :- Y is X * 2.");
        var sol = engine.Query("clause(double(A, B), Body).");
        Assert.True(sol.Success);
        // Body is `B is A * 2` — exact AST shape depends on parser, but it
        // must be a compound for is/2.
        var bodyCt = Assert.IsType<CompoundTerm>(sol["Body"]);
        Assert.Equal("is", bodyCt.Functor);
        Assert.Equal(2, bodyCt.Args.Length);
    }

    [Fact]
    public void Clause_DynamicFact_FoundAfterAssertz()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic note/1.");
        engine.Query("assertz(note(first)).");
        var sol = engine.Query("clause(note(X), _).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("first"), sol["X"]);
    }

    [Fact]
    public void Clause_NoMatchingPredicate_Fails()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(1).");
        // No predicate q/1 anywhere → clause/2 fails (doesn't throw in v1).
        Assert.False(engine.Query("clause(q(X), _).").Success);
    }

    // ---------- current_predicate/1 ----------

    [Fact]
    public void CurrentPredicate_StaticPredicate_Succeeds()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            foo(a).
            foo(b).
            """);
        Assert.True(engine.Query("current_predicate(foo/1).").Success);
    }

    [Fact]
    public void CurrentPredicate_DynamicPredicate_Succeeds()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic bar/2.");
        Assert.True(engine.Query("current_predicate(bar/2).").Success);
    }

    [Fact]
    public void CurrentPredicate_Builtin_Succeeds()
    {
        // Builtins are visible to current_predicate too (they live in the
        // "system" pseudo-module per ADR-008).
        var engine = new PrologEngine();
        // Library (prelude) and builtin predicates are not enumerated by
        // current_predicate/1 — §8.8.2 restricts it to user-defined
        // procedures, as GNU does. They ARE predicate_property built_in.
        Assert.True(engine.Query("\\+ current_predicate(append/3).").Success);
        Assert.True(engine.Query("\\+ current_predicate(is/2).").Success);
        Assert.True(engine.Query("predicate_property(append(_,_,_), built_in).").Success);
    }

    [Fact]
    public void CurrentPredicate_Unknown_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("current_predicate(definitely_not_a_predicate/7).").Success);
    }

    [Fact]
    public void CurrentPredicate_BadSpec_RaisesIsoError()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => engine.Query("current_predicate(not_a_slash)."));
        // Should be an ISO type_error.
        var ct = Assert.IsType<CompoundTerm>(ex.Term);
        Assert.Equal("error", ct.Functor);
        var inner = Assert.IsType<CompoundTerm>(ct.Args[0]);
        Assert.Equal("type_error", inner.Functor);
    }

    // ---------- abolish/1 ----------

    [Fact]
    public void Abolish_RemovesAllDynamicClauses()
    {
        var engine = new PrologEngine();
        // Phase 19+ — implicit_dynamic=false so this test exercises
        // the strict path (abolish makes the predicate fully
        // undeclared; the next assertz raises permission_error).
        // Under the default lenient path the next assertz would
        // simply re-promote the predicate.
        engine.Query("set_prolog_flag(implicit_dynamic, false).");
        engine.ConsultString(":- dynamic counter/1.");
        engine.Query("assertz(counter(1)).");
        engine.Query("assertz(counter(2)).");
        engine.Query("assertz(counter(3)).");

        Assert.True(engine.Query("counter(2).").Success);

        Assert.True(engine.Query("abolish(counter/1).").Success);

        // Predicate is gone: it's no longer in the dynamic registry, and
        // asserting again must re-declare it dynamic first. Phase-9
        // chunk 131e: raises catchable permission_error now.
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => engine.Query("assertz(counter(99))."));
        Assert.Equal("permission_error", ex.Kind);
    }

    [Fact]
    public void Abolish_NonexistentDynamic_SucceedsSilently()
    {
        // Per ISO, abolish on a never-declared predicate is a no-op success
        // (the engine has nothing to remove).
        var engine = new PrologEngine();
        Assert.True(engine.Query("abolish(never_existed/3).").Success);
    }

    [Fact]
    public void Abolish_BadSpec_RaisesIsoError()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => engine.Query("abolish(not_a_slash)."));
        var ct = Assert.IsType<CompoundTerm>(ex.Term);
        Assert.Equal("error", ct.Functor);
    }

    // ---------- Round trip ----------

    [Fact]
    public void Assertz_Then_CurrentPredicate_Sees()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic registered/1.");
        Assert.True(engine.Query("current_predicate(registered/1).").Success);
        engine.Query("assertz(registered(item)).");
        // current_predicate succeeds — registered/1 is in the dynamic set
        // regardless of how many clauses have been asserted.
        Assert.True(engine.Query("current_predicate(registered/1).").Success);
    }
}
