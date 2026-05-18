using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 39: Tier-0 → Tier-1 auto-promotion. The IL compiler's subset is
/// unchanged (single-clause facts with no body); the new piece is the
/// counter-driven promotion path that swaps a hot Tier-0 predicate's
/// dispatch for a Tier-1 IL delegate without losing semantics.
///
/// <para>Multi-clause IL promotion is intentionally still out of scope —
/// the IL compiler has no way to round-trip a Prolog choice point, so
/// promoting a predicate that's not provably deterministic would break
/// <c>findall/3</c> and friends. Future chunks will lift that, either by
/// extending IL to push CPs or by adding determinism inference.</para>
/// </summary>
public class Chunk39Tests
{
    private static Term Atom(string n) => new AtomTerm(n);

    private static int FunctorId(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    // ============================================================================
    // IlPromotionStore — counter, threshold, manual promotion
    // ============================================================================

    [Fact]
    public void Store_DisabledByDefault_DoesNotPromote()
    {
        var store = new IlPromotionStore();
        var pred = CompileSinglePredicate("greet(world).");
        // Threshold = 0 → RecordInvocation always returns null, never compiles.
        for (int i = 0; i < 50; i++)
            Assert.Null(store.RecordInvocation(pred.FunctorId, pred));
        Assert.False(store.IsPromoted(pred.FunctorId));
        Assert.Equal(0, store.CountFor(pred.FunctorId));
    }

    [Fact]
    public void Store_BelowThreshold_BumpsCounterButDoesntCompile()
    {
        var store = new IlPromotionStore { Threshold = 5 };
        var pred = CompileSinglePredicate("greet(world).");
        for (int i = 0; i < 4; i++) store.RecordInvocation(pred.FunctorId, pred);
        Assert.Equal(4, store.CountFor(pred.FunctorId));
        Assert.False(store.IsPromoted(pred.FunctorId));
    }

    [Fact]
    public void Store_AtThreshold_CompilesAndCaches()
    {
        var store = new IlPromotionStore { Threshold = 3 };
        var pred = CompileSinglePredicate("greet(world).");
        Assert.Null(store.RecordInvocation(pred.FunctorId, pred));   // 1
        Assert.Null(store.RecordInvocation(pred.FunctorId, pred));   // 2
        var del = store.RecordInvocation(pred.FunctorId, pred);      // 3 → compile
        Assert.NotNull(del);
        Assert.True(store.IsPromoted(pred.FunctorId));
        // Subsequent calls return the same cached delegate.
        var cached = store.TryGet(pred.FunctorId);
        Assert.Same(del, cached);
    }

    [Fact]
    public void Store_UnsupportedPredicate_MarkedUnpromotableNeverRetried()
    {
        // A predicate that calls a body goal is outside the IL subset.
        var store = new IlPromotionStore { Threshold = 1 };
        var pred = CompileSinglePredicate("rule(X) :- other(X).");
        Assert.Null(store.RecordInvocation(pred.FunctorId, pred));
        Assert.True(store.IsUnpromotable(pred.FunctorId));
        // Future attempts return null without re-trying compilation.
        for (int i = 0; i < 100; i++)
            Assert.Null(store.RecordInvocation(pred.FunctorId, pred));
    }

    [Fact]
    public void Store_Warm_PromotesEagerly()
    {
        var store = new IlPromotionStore();   // threshold still 0
        var pred = CompileSinglePredicate("greet(world).");
        var del = store.Warm(pred.FunctorId, pred);
        Assert.NotNull(del);
        Assert.True(store.IsPromoted(pred.FunctorId));
        // Subsequent TryGet returns the same delegate.
        Assert.Same(del, store.TryGet(pred.FunctorId));
    }

    [Fact]
    public void Store_Warm_OnUnpromotableReturnsNull()
    {
        var store = new IlPromotionStore();
        var pred = CompileSinglePredicate("rule(X) :- other(X).");
        Assert.Null(store.Warm(pred.FunctorId, pred));
        Assert.True(store.IsUnpromotable(pred.FunctorId));
    }

    // ============================================================================
    // Engine integration — promotion fires on hot predicates
    // ============================================================================

    [Fact]
    public void Engine_PromotionDisabled_NeverCallsIl()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- public greet/1.\ngreet(world).\n");
        // Threshold = 0 (default).
        for (int i = 0; i < 10; i++) Assert.True(engine.Query("greet(world).").Success);
        int fid = FunctorId("greet", 1);
        Assert.False(engine.IlPromotion.IsPromoted(fid));
    }

    [Fact]
    public void Engine_PromotionEnabled_PromotesAfterThresholdInvocations()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 3;
        engine.ConsultString(":- public greet/1.\ngreet(world).\n");
        int fid = FunctorId("greet", 1);

        // First two calls bump the counter; third call triggers compilation
        // inside the interpreter's Call dispatch.
        Assert.True(engine.Query("greet(world).").Success);
        Assert.False(engine.IlPromotion.IsPromoted(fid));
        Assert.True(engine.Query("greet(world).").Success);
        Assert.False(engine.IlPromotion.IsPromoted(fid));
        Assert.True(engine.Query("greet(world).").Success);
        Assert.True(engine.IlPromotion.IsPromoted(fid));
    }

    [Fact]
    public void Engine_PromotedPredicate_ProducesSameResultAsTier0()
    {
        // Run the same query before and after the promotion threshold.
        // The truth value and the binding should be identical — IL emission
        // must preserve unification semantics.
        var engineA = new PrologEngine();
        engineA.ConsultString(":- public answer/1.\nanswer(42).\n");
        var solA = engineA.Query("answer(X).");

        var engineB = new PrologEngine();
        engineB.IlPromotion.Threshold = 1;
        engineB.ConsultString(":- public answer/1.\nanswer(42).\n");
        var solB = engineB.Query("answer(X).");

        Assert.True(solA.Success);
        Assert.True(solB.Success);
        Assert.Equal(solA["X"], solB["X"]);
        Assert.True(engineB.IlPromotion.IsPromoted(FunctorId("answer", 1)));
    }

    [Fact]
    public void Engine_PromotedPredicate_FailingCallStillFails()
    {
        // Promote then call with a non-matching arg — must still report fail
        // (the IL has the same head-match-or-fail shape as the bytecode).
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public colour/1.\ncolour(red).\n");

        Assert.True(engine.Query("colour(red).").Success);
        Assert.True(engine.IlPromotion.IsPromoted(FunctorId("colour", 1)));
        Assert.False(engine.Query("colour(blue).").Success);
        // Re-run the matching arg through the promoted path.
        Assert.True(engine.Query("colour(red).").Success);
    }

    [Fact]
    public void Engine_UnpromotablePredicate_StaysOnTier0Forever()
    {
        // A rule with a body goal is outside the IL subset. The store sees
        // it once, marks it unpromotable, and never retries.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public greet/0.\ngreet :- write(hi).\n");
        for (int i = 0; i < 5; i++) engine.Query("greet.");
        int fid = FunctorId("greet", 0);
        Assert.True(engine.IlPromotion.IsUnpromotable(fid));
        Assert.False(engine.IlPromotion.IsPromoted(fid));
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private static CompiledPredicate CompileSinglePredicate(string source)
    {
        var clauses = new ClauseReader(new Lexer(source), OperatorTable.Default())
            .ReadAll()
            .Where(c => c.Kind != ClauseKind.Directive)
            .ToList();
        // All clauses belong to the same predicate in these test sources.
        return new PredicateCompiler().Compile(clauses);
    }
}
