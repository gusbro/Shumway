using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 76 — profile-guided optimisation (PGO) of IL code. The last
/// Phase-3 item. A multi-clause predicate that crosses the IL
/// promotion threshold is first compiled in <em>instrumented</em>
/// form: the indexed-atom ground dispatch records which atom matched.
/// Once enough samples accumulate, a query-setup pass recompiles the
/// predicate in <em>optimised</em> form — the ground-dispatch cmp
/// chain reordered so the most-frequently-matched atom is checked
/// first — and the instrumentation is dropped.
///
/// <para>The ground dispatch is a pure lookup (whichever atom
/// matches, the answer is identical), so the reorder is always
/// semantics-preserving. These tests pin the two-phase transition and
/// — most importantly — that answers are identical in every phase.</para>
/// </summary>
public class Chunk76Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    /// <summary>Builds an engine over an indexed-atom predicate
    /// (arity-1, all atom facts) with promotion + PGO thresholds set
    /// low so the phases transition quickly.</summary>
    private static PrologEngine NewColorEngine(int pgoThreshold)
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        // Phase 33 L2 — these tests pin the PGO PHASE MECHANICS, whose sample
        // counts are only query-count-deterministic under synchronous
        // promotion (background promotion leaves early queries on Tier-0,
        // where no instrumentation samples accumulate).
        engine.IlPromotion.BackgroundCompilation = false;
        engine.IlPromotion.PgoSampleThreshold = pgoThreshold;
        engine.ConsultString(
            ":- public color/1.\n" +
            "color(red).\n" +
            "color(green).\n" +
            "color(blue).\n" +
            "color(yellow).\n");
        return engine;
    }

    [Fact]
    public void IndexedAtomPredicate_PromotesToInstrumentedPhase1()
    {
        var engine = NewColorEngine(pgoThreshold: 100);
        engine.Query("color(red).");   // crosses promotion threshold
        int fid = Fid("color", 1);
        Assert.True(engine.IlPromotion.IsPromoted(fid));
        // Indexed-atom shape → it carries a profile, phase 1.
        Assert.True(engine.IlPromotion.IsPgoInstrumented(fid));
        Assert.False(engine.IlPromotion.IsPgoOptimized(fid));
    }

    [Fact]
    public void EnoughSamples_RecompilesToOptimisedPhase2()
    {
        var engine = NewColorEngine(pgoThreshold: 4);
        int fid = Fid("color", 1);
        // Each ground query adds one profile sample. The phase-2
        // recompile fires at the next query setup after the sample
        // count reaches the threshold.
        for (int i = 0; i < 8; i++) engine.Query("color(blue).");
        Assert.True(engine.IlPromotion.IsPgoOptimized(fid));
        Assert.False(engine.IlPromotion.IsPgoInstrumented(fid));
    }

    [Fact]
    public void Answers_IdenticalAcrossAllPhases()
    {
        var engine = NewColorEngine(pgoThreshold: 3);
        int fid = Fid("color", 1);

        // Phase 1 (instrumented): every atom resolves, unknowns fail.
        engine.Query("color(red).");
        Assert.True(engine.IlPromotion.IsPgoInstrumented(fid));
        Assert.True(engine.Query("color(red).").Success);
        Assert.True(engine.Query("color(yellow).").Success);
        Assert.False(engine.Query("color(purple).").Success);

        // Drive it into phase 2.
        for (int i = 0; i < 10; i++) engine.Query("color(blue).");
        Assert.True(engine.IlPromotion.IsPgoOptimized(fid));

        // Phase 2 (optimised, reordered): same answers, including the
        // cold atoms that moved to the back of the cmp chain.
        Assert.True(engine.Query("color(red).").Success);
        Assert.True(engine.Query("color(green).").Success);
        Assert.True(engine.Query("color(blue).").Success);
        Assert.True(engine.Query("color(yellow).").Success);
        Assert.False(engine.Query("color(purple).").Success);
    }

    [Fact]
    public void OptimisedPredicate_StillEnumeratesViaBacktracking()
    {
        // PGO only reorders the ground (bound-arg) dispatch; the
        // var-dispatch enumeration path keeps source order, so an
        // unbound query still yields every clause in order.
        var engine = NewColorEngine(pgoThreshold: 3);
        for (int i = 0; i < 10; i++) engine.Query("color(blue).");
        Assert.True(engine.IlPromotion.IsPgoOptimized(Fid("color", 1)));
        var sols = engine.QueryAll("color(X).").Select(s => s["X"]).ToList();
        Assert.Equal(
            new Term[]
            {
                new AtomTerm("red"), new AtomTerm("green"),
                new AtomTerm("blue"), new AtomTerm("yellow"),
            },
            sols);
    }

    [Fact]
    public void HotAtom_RemainsCorrectAfterReorder()
    {
        // blue is queried far more than any other atom, so the phase-2
        // reorder puts its cmp first. The cold atoms must still resolve.
        var engine = NewColorEngine(pgoThreshold: 5);
        int fid = Fid("color", 1);
        for (int i = 0; i < 20; i++) engine.Query("color(blue).");
        Assert.True(engine.IlPromotion.IsPgoOptimized(fid));
        // The hot atom and every cold atom resolve from the reordered
        // chain.
        Assert.True(engine.Query("color(blue).").Success);
        Assert.True(engine.Query("color(red).").Success);
        Assert.True(engine.Query("color(yellow).").Success);
    }

    [Fact]
    public void NonIndexedAtomPredicate_PromotesWithoutProfile()
    {
        // A single-clause predicate isn't the indexed-atom shape — it
        // promotes (chunk 25) but carries no PGO profile.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public greet/0.\ngreet.");
        engine.Query("greet.");
        int fid = Fid("greet", 0);
        Assert.True(engine.IlPromotion.IsPromoted(fid));
        Assert.False(engine.IlPromotion.IsPgoInstrumented(fid));
        Assert.False(engine.IlPromotion.IsPgoOptimized(fid));
    }

    [Fact]
    public void PgoSampleThreshold_GovernsTheTransition()
    {
        var engine = NewColorEngine(pgoThreshold: 6);
        int fid = Fid("color", 1);
        // Five ground calls — below the threshold, still instrumented.
        for (int i = 0; i < 5; i++) engine.Query("color(red).");
        Assert.True(engine.IlPromotion.IsPgoInstrumented(fid));
        // A few more cross it; the next query setup recompiles.
        for (int i = 0; i < 4; i++) engine.Query("color(red).");
        Assert.True(engine.IlPromotion.IsPgoOptimized(fid));
    }

    [Fact]
    public void GroundDispatch_StaysDeterministicAfterPgo()
    {
        // A ground call to an optimised indexed-atom predicate is
        // deterministic — exactly one solution, no spurious choice
        // point that would yield a second.
        var engine = NewColorEngine(pgoThreshold: 3);
        for (int i = 0; i < 10; i++) engine.Query("color(green).");
        Assert.True(engine.IlPromotion.IsPgoOptimized(Fid("color", 1)));
        Assert.Single(engine.QueryAll("color(green)."));
    }
}
