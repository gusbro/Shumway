using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 420 — automatic dead-chain reclamation fires by dead count alone
/// (the old <c>dead &lt; live</c> gate let a busy predicate sit permanently at
/// ~live-count tombstones that every read walked). These tests distil the
/// unget-buffer / mutable-fact idioms whose chain bookkeeping is easy to
/// corrupt — S2 is the exact shape that exposed the chunk-404 interpreter-side
/// unlink as unsound (bytecode pointers patched behind the C#-side
/// DynChainEntry records, so the next assertz appended to a bypassed tail and
/// the clause vanished). The reclamation path re-threads FROM the records, so
/// both stay in lockstep; these pin that contract under churn well past the
/// reclaim threshold.
/// </summary>
public class Chunk420Tests
{
    private const string Buffer =
        ":- dynamic b/1.\n" +
        "front(C) :- asserta(b(C)).\n" +
        "back(C)  :- assertz(b(C)).\n" +
        "take(C)  :- retract(b(C)), !.\n" +
        "dump([])    :- \\+ call(b(_)), !.\n" +
        "dump([C|T]) :- retract(b(C)), !, dump(T).\n";

    private static PrologEngine Make(string extra = "")
    {
        var e = new PrologEngine();
        e.ConsultString(Buffer + extra);
        return e;
    }

    [Fact]
    public void AssertzAfterTombstones_StaysLinked()
    {
        // The chunk-404 killer (fuzz S2): two tombstones at the chain head,
        // two front-pushes, then a BACK-push. If the chain bytecode and the
        // C#-side tail bookkeeping ever diverge, z lands on a dead branch
        // and vanishes.
        var e = Make();
        var s = e.Query(
            "back(1), back(2), take(_), take(_), " +
            "front(x), front(y), back(z), dump(R).");
        Assert.True(s.Success);
        Assert.Equal(".(y, .(x, .(z, [])))", s["R"]!.ToString());
    }

    [Fact]
    public void ChurnPastThreshold_QueueOrderSurvives()
    {
        // 200 take+back rounds (≥ 6 reclaims at threshold 32) keeping two
        // live clauses; FIFO rotation must hold throughout.
        var e = Make(
            "loop(0) :- !.\n" +
            "loop(N) :- take(C), back(C), M is N - 1, loop(M).\n");
        var s = e.Query("back(a), back(b), loop(200), dump(R).");
        Assert.True(s.Success);
        Assert.Equal(".(a, .(b, []))", s["R"]!.ToString());
        Assert.True(e.ChainReclaims >= 5,
            $"reclaim should fire under churn (fired {e.ChainReclaims}x)");
    }

    [Fact]
    public void ChurnWithFrontPushes_HeadDemotionSurvives()
    {
        // Mixed asserta/assertz churn (fuzz S4): every 7th round re-inserts
        // at the FRONT, exercising head demotion + reclamation together.
        var e = Make(
            "loop(0) :- !.\n" +
            "loop(N) :- take(C), ( 0 is N mod 7 -> front(C) ; back(C) ),\n" +
            "           M is N - 1, loop(M).\n");
        var s = e.Query("back(a), back(b), back(c), loop(150), dump(R).");
        Assert.True(s.Success);
        // 150 rounds of deterministic rotation: the multiset survives.
        var r = s["R"]!.ToString()!;
        Assert.Contains("a", r);
        Assert.Contains("b", r);
        Assert.Contains("c", r);
        Assert.Equal(3, r.Count(ch => ch is 'a' or 'b' or 'c'));
    }

    [Fact]
    public void ManyLiveClauses_ReclaimStillFires()
    {
        // The chunk-420 fix proper: 100 LIVE clauses + churn. The old
        // dead < Entries.Count gate never reclaimed here (dead capped at
        // the threshold but live stayed higher), leaving every read to
        // walk ~live-count tombstones forever.
        var e = Make(
            "fill(0) :- !.\n" +
            "fill(N) :- back(N), M is N - 1, fill(M).\n" +
            "churn(0) :- !.\n" +
            "churn(N) :- take(C), back(C), M is N - 1, churn(M).\n");
        var s = e.Query("fill(100), churn(120), ( call(b(X)) -> true ; X = none ).");
        Assert.True(s.Success);
        Assert.True(e.ChainReclaims >= 1,
            $"reclaim must fire with live > dead (fired {e.ChainReclaims}x)");
    }

    [Fact]
    public void SuspendedEnumeration_LogicalViewSurvivesChurn()
    {
        // A chain CP suspended in b/1 while churn invites reclamation:
        // the safety scan must hold the reclaim back, and the suspended
        // enumeration must still see its captured view (fuzz S5 widened
        // past the threshold).
        var e = Make(
            "churn(0) :- !.\n" +
            "churn(N) :- take(C), back(C), M is N - 1, churn(M).\n" +
            "probe(X) :- call(b(X)), churn(60), true.\n");
        var all = e.QueryAll("back(1), back(2), back(3), probe(X).").ToList();
        // The enumeration's view is fixed at its start: it must yield the
        // three clauses that existed then, regardless of the churn each
        // solution triggers afterwards.
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void TokenizerRhythm_TwoCharLookahead()
    {
        // peek/take/unget ×2 (fuzz S6) — the Blint s_peek/s_get/unget shape.
        var e = Make(
            "peek(C) :- ( call(b(X)) -> C = X ; C = empty ).\n");
        var s = e.Query(
            "back(p), back(q), back(r), " +
            "peek(A), take(A1), peek(B), take(B1), " +
            "front(B1), front(A1), dump(R), " +
            "L = [A, A1, B, B1 | R].");
        Assert.True(s.Success);
        Assert.Equal(".(p, .(p, .(q, .(q, .(p, .(q, .(r, [])))))))",
            s["L"]!.ToString());
    }

    [Fact]
    public void DeepTombstones_ThenSingleLiveRead()
    {
        // 120 back+take pairs (pure tombstone production, ≥3 reclaims),
        // then one live clause asserted at the end must be found.
        var e = Make(
            "fillchurn(0) :- !, back(last).\n" +
            "fillchurn(N) :- back(N), take(_), M is N - 1, fillchurn(M).\n");
        var s = e.Query("fillchurn(120), take(A), dump(R).");
        Assert.True(s.Success);
        Assert.Equal("last", s["A"]!.ToString());
        Assert.Equal("[]", s["R"]!.ToString());
    }
}
