using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 404 (Phase 29) — in-place dead-chain compaction. A retract leaves a
/// died-patched tombstone in the dynamic predicate's trampoline chain; chunk 403's
/// profiling showed a single assert/retract-stack predicate (Blint's
/// saved_cur_line_i/2) re-scanning 1.5M tombstones in one query — O(N²), because the
/// chunk-158 auto-compact only runs BETWEEN queries. The interpreter now bypasses
/// consecutive tombstones whose <c>died &lt;= Engine.MinLiveViewGen()</c> (invisible to
/// every live choice point AND every future call) by patching the predecessor entry's
/// next operand in place — the chunk-127/128 mechanism.
///
/// The tests pin BOTH sides: the unlink fires (the structural win), and — the
/// soundness crux — it does NOT fire while an older choice point's logical update
/// view can still see the retracted clause (ISO logical update view).
/// </summary>
public class Chunk404Tests
{
    /// <summary>The O(N²) shape: a long assert/retract churn on one dynamic
    /// predicate inside ONE query, then enumeration must see exactly the survivors —
    /// and the engine must have unlinked tombstones along the way.</summary>
    [Fact]
    public void ChurnedChain_Compacts_AndStaysCorrect()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic d/1.\n");
        // One query: 300 × (assertz + retract) churn, then two survivors, then check.
        // between/3 drives the loop; the retract leaves a tombstone each iteration.
        bool ok = engine.Query(
            "( between(1, 300, I), assertz(d(I)), retract(d(I)), fail ; true ),"
            + "assertz(d(alpha)), assertz(d(beta)),"
            + "findall(X, d(X), L), L == [alpha, beta].").Success;
        Assert.True(ok);
    }

    /// <summary>The soundness discriminator (verified to FAIL under a
    /// live-view-unaware unlink). Open a choice point into the chain (the e(X)
    /// enumeration suspended at X=1, its view V), retract e(2..4) — tombstones
    /// for NEW views, but V still sees them (ISO logical update view). Then a
    /// NEW-view sweep (findall over e/1, which sees only e(1)) traverses
    /// e(2..4) as tombstones and invites compaction: it stands on TOMBSTONE
    /// e(2) and would patch ITS next operand past e(3)/e(4) — pointers the OLD
    /// enumeration must still follow, because when it resumes at e(2) (visible
    /// to V!) it re-reads e(2)'s next to advance. The MinLiveViewGen guard
    /// (V &lt; died) must therefore refuse the unlink: the old enumeration
    /// yields 2, 3, 4 after the sweep. A guard that only checks "is it a
    /// tombstone" loses 3 and 4.</summary>
    [Fact]
    public void OldChoicePointView_SurvivesCompactionAttempts()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic e/1.\n:- dynamic out/1.\n");
        bool ok = engine.Query(
            "assertz(e(1)), assertz(e(2)), assertz(e(3)), assertz(e(4)),"
            + "( e(X),"
            + "  ( X == 1 -> retract(e(2)), retract(e(3)), retract(e(4)),"
            + "    findall(Y, e(Y), New), New == [1]"
            + "  ; true ),"
            + "  assertz(out(X)), fail"
            + "; true ),"
            + "findall(X, out(X), L), L == [1, 2, 3, 4].").Success;
        Assert.True(ok);
    }

    /// <summary>Structural assertion that the unlink actually FIRES on the churn
    /// shape (guards against the optimization silently never running). Uses the
    /// engine-level counter; the churn must run inside a single query.</summary>
    [Fact]
    public void Unlinks_AreCounted()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic f/1.\n");
        long before = TotalUnlinks(engine);
        Assert.True(engine.Query(
            "( between(1, 200, I), assertz(f(I)), retract(f(I)), fail ; true ),"
            + "assertz(f(last)), f(last).").Success);
        long after = TotalUnlinks(engine);
        Assert.True(after > before,
            $"expected dead-chain unlinks to fire (before={before}, after={after})");
    }

    private static long TotalUnlinks(PrologEngine engine) => engine.DeadChainUnlinks;
}
