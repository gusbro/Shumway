using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ADR-023 — a read-hot, mutation-cold `:- dynamic` predicate is promoted to
// Tier-1 IL as a snapshot of its visible clauses; any mutation evicts the
// snapshot (back to the in-place-patched Tier-0 bytecode); past the churn limit it
// is pinned to Tier 0. The logical update view (ADR-015) is preserved.
public sealed class DynamicIlPromotionTests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static PrologEngine Activation(string program, int threshold = 1)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = threshold;
        // Deterministic promotion: with the background worker, whether a
        // delegate is INSTALLED by the time a mutation evicts depends on
        // compile timing — and EvictDelegate counts churn only when a delegate
        // was actually present. Under a cold JIT (standalone run) or CPU
        // contention (parallel gate) the install could miss the round, the
        // eviction went uncounted, and the churn-pin assertions flaked.
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(program);
        return e;
    }

    [Fact]
    public void ReadHotDynamic_PromotesToIl_AndRunsCorrectly()
    {
        var e = Activation(":- dynamic color/1.\ncolor(red).\ncolor(green).\ncolor(blue).\n");
        int fid = Fid("color", 1);

        for (int i = 0; i < 5; i++)
            Assert.True(e.Query("color(green).").Success);

        Assert.True(e.IlPromotion.IsPromoted(fid));                 // promoted as a snapshot
        Assert.True(e.Query("findall(X, color(X), L), length(L, N), N == 3.").Success);
        Assert.False(e.Query("color(yellow).").Success);
    }

    [Fact]
    public void Mutation_EvictsSnapshot_AndReflectsNewState()
    {
        var e = Activation(":- dynamic color/1.\ncolor(red).\ncolor(green).\ncolor(blue).\n");
        int fid = Fid("color", 1);

        for (int i = 0; i < 5; i++) Assert.True(e.Query("color(green).").Success);
        Assert.True(e.IlPromotion.IsPromoted(fid));

        Assert.True(e.Query("assertz(color(yellow)).").Success);    // mutation
        Assert.False(e.IlPromotion.IsPromoted(fid));               // snapshot evicted

        // Tier-0 bytecode (patched in place) reflects the new clause.
        Assert.True(e.Query("color(yellow).").Success);
        Assert.True(e.Query("findall(X, color(X), L), length(L, N), N == 4.").Success);

        // Re-warms and re-promotes the new snapshot.
        for (int i = 0; i < 5; i++) Assert.True(e.Query("color(yellow).").Success);
        Assert.True(e.IlPromotion.IsPromoted(fid));
    }

    [Fact]
    public void Retract_EvictsAndReflectsRemoval()
    {
        var e = Activation(":- dynamic n/1.\nn(1).\nn(2).\nn(3).\n");
        int fid = Fid("n", 1);
        for (int i = 0; i < 5; i++) Assert.True(e.Query("n(2).").Success);
        Assert.True(e.IlPromotion.IsPromoted(fid));

        Assert.True(e.Query("retract(n(2)).").Success);
        Assert.False(e.IlPromotion.IsPromoted(fid));
        Assert.False(e.Query("n(2).").Success);                    // gone
        Assert.True(e.Query("findall(X, n(X), L), L == [1, 3].").Success);
    }

    [Fact]
    public void ChurnGuard_PinsToTier0AfterRepeatedMutation()
    {
        var e = Activation(":- dynamic d/1.\nd(0).\n");
        int fid = Fid("d", 1);
        // each round: warm to promotion, then mutate to evict. Past the churn
        // limit (3) the predicate stays on Tier 0 even when hot. (Phase 33 L5:
        // the pin is expressed by the eviction count — re-armable — rather than
        // the permanent _unpromotable set; keep the read stretch below
        // ChurnRearmCalls so it stays pinned here.)
        for (int round = 0; round < 6; round++)
        {
            for (int i = 0; i < 5; i++) Assert.True(e.Query("d(0).").Success);
            Assert.True(e.Query("assertz(d(0)).").Success);
        }
        for (int i = 0; i < 10; i++) Assert.True(e.Query("d(0).").Success);
        Assert.False(e.IlPromotion.IsPromoted(fid));               // pinned to Tier 0
    }

    [Fact]
    public void ChurnGuard_RearmsAfterMutationFreeStretch()
    {
        // Phase 33 L5 — the Arity load-mutate-then-read-forever profile: a
        // predicate churn-pinned during its startup mutation phase re-arms after
        // a long mutation-free read stretch and earns IL again.
        var e = Activation(":- dynamic d/1.\nd(0).\n");
        e.IlPromotion.ChurnRearmCalls = 20;   // short streak for the test
        int fid = Fid("d", 1);
        for (int round = 0; round < 6; round++)
        {
            for (int i = 0; i < 5; i++) Assert.True(e.Query("d(0).").Success);
            Assert.True(e.Query("assertz(d(0)).").Success);
        }
        Assert.False(e.IlPromotion.IsPromoted(fid));   // pinned by churn
        // Mutation-free reads: pin re-arms after ChurnRearmCalls, then the
        // (primed) predicate re-promotes; results stay correct throughout.
        for (int i = 0; i < 60; i++) Assert.True(e.Query("d(0).").Success);
        Assert.True(e.IlPromotion.IsPromoted(fid));
        Assert.True(e.Query("findall(X, d(X), L), length(L, N), N == 7.").Success);
        // A returning mutation phase evicts + one more churn re-pins quickly.
        Assert.True(e.Query("assertz(d(9)).").Success);
        Assert.False(e.IlPromotion.IsPromoted(fid));
        Assert.True(e.Query("d(9).").Success);
    }

    [Fact]
    public void DeclaredDynamicWithClauses_PrimesOnFirstCall()
    {
        // ADR-023 priming — a `:- dynamic` (or `:- visible`) predicate declared
        // WITH clauses promotes to its IL snapshot on the FIRST call, even under a
        // far-away warm-up threshold (other predicates would need `threshold`
        // calls). It stays fully mutable + evictable.
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1000;   // promotion ON, normal warm-up far away
        e.ConsultString(":- dynamic color/1.\ncolor(red).\ncolor(green).\ncolor(blue).\n");
        int fid = Fid("color", 1);
        Assert.False(e.IlPromotion.IsPromoted(fid));
        Assert.True(e.Query("color(green).").Success);   // ONE call
        Assert.True(e.IlPromotion.IsPromoted(fid));       // primed → already IL
        // unchanged mutability: a mutation evicts the snapshot, new state is live.
        Assert.True(e.Query("assertz(color(yellow)).").Success);
        Assert.False(e.IlPromotion.IsPromoted(fid));
        Assert.True(e.Query("color(yellow).").Success);
    }

    [Fact]
    public void RuntimeOnlyDynamic_NotPrimed_WarmsNormally()
    {
        // A dynamic predicate with NO source clauses (populated only by runtime
        // assertz) is NOT primed — under a high threshold one call won't promote it.
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1000;
        e.ConsultString(":- dynamic t/1.\n");
        Assert.True(e.Query("assertz(t(1)).").Success);
        int fid = Fid("t", 1);
        Assert.True(e.Query("t(1).").Success);
        Assert.False(e.IlPromotion.IsPromoted(fid));
    }

    [Fact]
    public void DynamicWithClauses_GetsBuildTimeSnapshotModule()
    {
        // ShmoCompiler bakes a static-style WAM snapshot of a dynamic predicate's
        // clauses so --dump-wam / --dump-il can show them (their clauses live in
        // DynamicSeeds, leaving the static module empty for them).
        var obj = ShmoCompiler.CompileSource(
            ":- dynamic d/1.\nd(1).\nd(2).\n", "m", ShmoBuildMode.Debug);
        Assert.NotNull(obj.DynamicSnapshotBytecode);
        var snap = CompiledModuleCodec.Decode(obj.DynamicSnapshotBytecode!);
        Assert.Contains(snap.Predicates, p => p.Arity == 1);   // d/1 snapshot present
    }

    [Fact]
    public void LogicalUpdateView_HoldsThroughIlSnapshot()
    {
        // d/1 is IL-promoted; a goal that backtracks over d/1 and asserts a new
        // clause MID-ITERATION must still see only the snapshot as of when its goal
        // began (ADR-015) — the in-progress call finishes on the snapshot delegate;
        // the assert evicts it only for FUTURE calls.
        var e = Activation(
            ":- dynamic d/1.\nd(1).\nd(2).\nd(3).\n" +
            "iter(L) :- findall(X, (d(X), (X =:= 2 -> assertz(d(99)) ; true)), L).\n");
        int fid = Fid("d", 1);
        for (int i = 0; i < 5; i++) Assert.True(e.Query("d(2).").Success);
        Assert.True(e.IlPromotion.IsPromoted(fid));                // snapshot active

        // iter sees [1,2,3] — NOT 99 (asserted during the iteration).
        Assert.True(e.Query("iter(L), L == [1, 2, 3].").Success);
        // but the assert did take effect for later calls.
        Assert.True(e.Query("d(99).").Success);
    }
}
