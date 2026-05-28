using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// implicit_dynamic auto-promotion must hold across consecutive
/// top-level queries — the REPL scenario where the user types
/// <c>?- asserta(pepe).</c> at one prompt and <c>?- call(pepe).</c>
/// (or just <c>?- pepe.</c>) at the next. Phase 19+'s consult-time
/// pre-scan is the workhorse for single-clause-body cases; this
/// suite verifies the runtime EnsureDynamic path delivers the same
/// guarantee when the assertz happens at top-level (no consulted
/// clause body to scan upfront).
/// </summary>
public class ImplicitDynamicCrossQueryTests
{
    [Fact]
    public void TopLevelAssertz_ThenCall_AnotherQuery()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("assertz(pepe).").Success);
        Assert.True(engine.Query("call(pepe).").Success);
    }

    [Fact]
    public void TopLevelAsserta_ThenCall_AnotherQuery()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("asserta(juan).").Success);
        Assert.True(engine.Query("call(juan).").Success);
    }

    [Fact]
    public void TopLevelAssert_ThenCall_AnotherQuery()
    {
        // `assert` is an alias for `assertz`.
        var engine = new PrologEngine();
        Assert.True(engine.Query("assert(roque).").Success);
        Assert.True(engine.Query("call(roque).").Success);
    }

    [Fact]
    public void TopLevelAssertz_ThenDirectCall_NoCallWrapper()
    {
        // Calling the predicate directly (not through call/1) after
        // top-level assertz. The chunk-205 static rewrite means the
        // bare `pepe` would compile to a direct Call opcode — only
        // works if the link knows pepe is dynamic.
        var engine = new PrologEngine();
        Assert.True(engine.Query("assertz(pepe).").Success);
        Assert.True(engine.Query("pepe.").Success);
    }

    [Fact]
    public void TopLevelAssertz_ThenCall_SameQuery()
    {
        // assertz and call in the SAME query body — the pre-scan of
        // the synthetic __query__ clause should pick up the literal
        // head and pre-declare it dynamic.
        var engine = new PrologEngine();
        Assert.True(engine.Query("assertz(pepe), call(pepe).").Success);
    }

    [Fact]
    public void TopLevelAsserta_ThenDirectCall_SameQuery()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("asserta(juan), juan.").Success);
    }

    [Fact]
    public void MultipleAssertz_AcrossQueries_AllCallable()
    {
        var engine = new PrologEngine();
        engine.Query("assertz(fact(1)).");
        engine.Query("assertz(fact(2)).");
        engine.Query("assertz(fact(3)).");
        // Enumerate all three.
        var sol1 = engine.Query("fact(X), X =:= 1.");
        Assert.True(sol1.Success);
        var sol2 = engine.Query("fact(X), X =:= 2.");
        Assert.True(sol2.Success);
        var sol3 = engine.Query("fact(X), X =:= 3.");
        Assert.True(sol3.Success);
    }

    [Fact]
    public void Assertz_ThenRetract_AcrossQueries()
    {
        var engine = new PrologEngine();
        engine.Query("assertz(item(a)).");
        engine.Query("assertz(item(b)).");
        Assert.True(engine.Query("retract(item(a)).").Success);
        // a is gone; b is still there.
        Assert.False(engine.Query("item(a).").Success);
        Assert.True(engine.Query("item(b).").Success);
    }
}
