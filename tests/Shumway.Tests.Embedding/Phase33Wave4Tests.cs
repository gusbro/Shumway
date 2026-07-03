using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 wave 4 — IL promotion pipeline (docs/phase-33-backlog.md, L-series).
/// L2: compiles run on the shared persistent worker; opt-in background mode keeps
/// the query thread unstalled. L1 (Stage B.4): a mid-query promotion patches the
/// callee's remaining generic call sites to CallIl/ExecuteIl.
/// </summary>
public class Phase33Wave4Tests
{
    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    // ---- L2 sync default: promotion still deterministic (worker-backed) ----

    // NB: the promotable predicates below are 2-clause on purpose — a local
    // single-clause pure rule gets UNFOLDED into its caller at query setup
    // (MetaWrapperUnfold), so no Call ever dispatches and nothing promotes.

    [Fact]
    public void L2_SyncDefault_PromotesDeterministically()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 3;
        e.ConsultString(":- public inc/2.\ninc(0, 1).\ninc(X, Y) :- X > 0, Y is X + 1.\n");
        int fid = Fid("inc", 2);
        for (int i = 0; i < 5; i++)
            Assert.True(e.Query("inc(1, Y), Y == 2.").Success);
        // Default mode: the threshold-crossing call waited for the compile.
        Assert.True(e.IlPromotion.IsPromoted(fid));
        Assert.True(e.Query("inc(41, Y), Y == 42.").Success);
    }

    // ---- L2 background: the query thread never waits; the delegate installs
    //      at a later dispatch (or the explicit barrier). ----

    [Fact]
    public void L2_Background_PromotesWithoutStalling_AndStaysCorrect()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 3;
        e.IlPromotion.BackgroundCompilation = true;
        e.ConsultString(":- public inc/2.\ninc(0, 1).\ninc(X, Y) :- X > 0, Y is X + 1.\n");
        int fid = Fid("inc", 2);
        // Results are correct from the first call regardless of when the
        // delegate lands.
        for (int i = 0; i < 10; i++)
            Assert.True(e.Query("inc(1, Y), Y == 2.").Success);
        // Barrier: wait for the queued compile, then it must be installed.
        Assert.True(e.IlPromotion.WaitForPendingPromotions());
        // The install happens at drain time (inside dispatch or the barrier).
        Assert.True(e.IlPromotion.IsPromoted(fid) || RunOnceMore(e, fid));
        Assert.True(e.Query("inc(41, Y), Y == 42.").Success);
    }

    private static bool RunOnceMore(PrologEngine e, int fid)
    {
        Assert.True(e.Query("inc(1, Y), Y == 2.").Success);
        return e.IlPromotion.IsPromoted(fid);
    }

    // ---- L2 background + mutation while in flight: the stale dynamic snapshot
    //      must NOT install over the logical-update view. ----

    [Fact]
    public void L2_Background_MutationInvalidatesInFlightSnapshot()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 2;
        e.IlPromotion.BackgroundCompilation = true;
        e.ConsultString(":- dynamic d/1.\nd(1).\n");
        // Warm to the threshold (queues a snapshot compile), then mutate
        // immediately — whatever the interleaving, results must reflect the
        // mutation afterwards.
        for (int i = 0; i < 5; i++) Assert.True(e.Query("d(1).").Success);
        Assert.True(e.Query("assertz(d(2)).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions());
        Assert.True(e.Query("d(2).").Success);
        Assert.True(e.Query("findall(X, d(X), L), L == [1, 2].").Success);
        // And it can still re-promote with the CURRENT clauses afterwards.
        for (int i = 0; i < 10; i++) Assert.True(e.Query("d(2).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions());
        Assert.True(e.Query("findall(X, d(X), L), L == [1, 2].").Success);
    }

    // ---- L3: the 16KB Sigil size cap relaxes to 64KB under background
    //      compilation (a long emit is latency off-thread, not a query stall). ----

    [Fact]
    public void L3_BackgroundMode_RaisesSizeCap()
    {
        // ~1200 atom facts ≈ 24 KB bytecode: above the 16 KB sync cap, below
        // the 64 KB background cap.
        var sb = new System.Text.StringBuilder(":- public big/1.\n");
        for (int i = 0; i < 1200; i++) sb.Append("big(a").Append(i).Append(").\n");
        string program = sb.ToString();

        // Synchronous mode: excluded by size — stays Tier-0, still correct.
        // (Phase 33 L2 flipped the default to background; this test's whole
        // point is the sync-vs-background cap difference, so pin it.)
        var sync = new PrologEngine();
        sync.IlPromotion.Threshold = 1;
        sync.IlPromotion.BackgroundCompilation = false;
        sync.ConsultString(program);
        int fidSync = Fid("big", 1);
        Assert.True(sync.Query("big(a5).").Success);
        Assert.False(sync.IlPromotion.IsPromoted(fidSync));

        // Background mode: allowed — compiles off-thread, promotes, stays correct.
        var bg = new PrologEngine();
        bg.IlPromotion.Threshold = 1;
        bg.IlPromotion.BackgroundCompilation = true;
        bg.ConsultString(program);
        int fid = Fid("big", 1);
        Assert.True(bg.Query("big(a5).").Success);
        Assert.True(bg.IlPromotion.WaitForPendingPromotions(120_000),
            "background compile of the large predicate timed out");
        Assert.True(bg.IlPromotion.IsPromoted(fid));
        Assert.True(bg.Query("big(a1199).").Success);
        Assert.False(bg.Query("big(zzz).").Success);
    }

    // ---- L1 (Stage B.4): a promotion that happens MID-QUERY patches the
    //      remaining generic call sites; the rest of the query stays correct. ----

    [Fact]
    public void L1_MidQueryPromotion_PatchesSites_AndStaysCorrect()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 10;
        e.ConsultString(
            ":- public sumd/3.\n" +
            "sumd(0, A, A) :- !.\n" +
            "sumd(N, A, S) :- N > 0, D is N * 2, A2 is A + D, N2 is N - 1, sumd(N2, A2, S).\n");
        int fid = Fid("sumd", 3);
        Assert.False(e.IlPromotion.IsPromoted(fid));
        // ONE query recursing 100 deep — the self-call dispatches per iteration,
        // crosses the threshold MID-QUERY, promotes, and (Stage B.4) the
        // persistent-buffer self-call site is patched to ExecuteIl/CallIl for the
        // remaining recursion. sum(2*i, i=1..100) = 10100 must still come out.
        Assert.True(e.Query("sumd(100, 0, S), S == 10100.").Success);
        Assert.True(e.IlPromotion.IsPromoted(fid));
        // Subsequent queries keep working through the patched persistent code.
        Assert.True(e.Query("sumd(10, 0, S), S == 110.").Success);
    }
}
