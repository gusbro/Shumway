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

    private static PrologEngine Engine(string program, int threshold = 1)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = threshold;
        e.ConsultString(program);
        return e;
    }

    [Fact]
    public void ReadHotDynamic_PromotesToIl_AndRunsCorrectly()
    {
        var e = Engine(":- dynamic color/1.\ncolor(red).\ncolor(green).\ncolor(blue).\n");
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
        var e = Engine(":- dynamic color/1.\ncolor(red).\ncolor(green).\ncolor(blue).\n");
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
        var e = Engine(":- dynamic n/1.\nn(1).\nn(2).\nn(3).\n");
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
        var e = Engine(":- dynamic d/1.\nd(0).\n");
        int fid = Fid("d", 1);
        // each round: warm to promotion, then mutate to evict. Past the churn
        // limit (3) the predicate stays on Tier 0 even when hot.
        for (int round = 0; round < 6; round++)
        {
            for (int i = 0; i < 5; i++) Assert.True(e.Query("d(0).").Success);
            Assert.True(e.Query("assertz(d(0)).").Success);
        }
        for (int i = 0; i < 10; i++) Assert.True(e.Query("d(0).").Success);
        Assert.False(e.IlPromotion.IsPromoted(fid));               // pinned to Tier 0
        Assert.True(e.IlPromotion.IsUnpromotable(fid));
    }

    [Fact]
    public void LogicalUpdateView_HoldsThroughIlSnapshot()
    {
        // d/1 is IL-promoted; a goal that backtracks over d/1 and asserts a new
        // clause MID-ITERATION must still see only the snapshot as of when its goal
        // began (ADR-015) — the in-progress call finishes on the snapshot delegate;
        // the assert evicts it only for FUTURE calls.
        var e = Engine(
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
