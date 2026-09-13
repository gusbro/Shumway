using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Removing a clause from an array-backed list shifts everything
/// above it, so draining a big predicate from the head was quadratic three
/// times over -- once in the store's clause list, once in the index's
/// sequence numbers, once in the chain's entries. A retract now leaves a
/// TOMBSTONE (the store) or retires the entry in place (the chain), and the
/// slots close up in a compaction, the same shape as the chain's dead chunks
/// and the linker's dead regions.
///
/// <para>The tombstones are strictly internal: every reader compacts the slot
/// before looking, so outside the store the list is always dense and a
/// tombstone cannot reach anything that shows a clause to a program. Only the
/// retract path walks over them, and skips them by reference.</para>
///
/// <para>Compaction is PROPORTIONAL -- when the tombstones reach half the
/// slot -- because compacting every K retracts is n/K passes of O(n), still
/// quadratic. Proportional makes each pass pay for the retracts that caused
/// it, and it is also what keeps a query that never returns compacting: the
/// safe-point compaction at query setup alone would never run for it.</para></summary>
public sealed class ClauseTombstoneTests
{
    private const string Program = """
        :- dynamic(tok/1).
        mk(0) :- !.
        mk(N) :- assertz(tok(N)), M is N - 1, mk(M).
        drain(0) :- !.
        drain(N) :- retract(tok(N)), M is N - 1, drain(M).
        """;

    /// <summary>COUNTED, not timed: a drain of n compacts O(log n) times --
    /// the halving trigger gives 15/16/17 at 32,000/64,000/128,000 -- where a
    /// periodic trigger gives O(n) passes and eager removal shifted on every
    /// single retract. More than a few dozen compactions here means the
    /// trigger stopped being proportional.</summary>
    [Fact]
    public void ADrainCompactsLogarithmicallyManyTimes()
    {
        foreach (int n in new[] { 2_000, 16_000 })
        {
            var e = new PrologEngine { Out = new StringWriter() };
            e.ConsultString(Program);
            e.ConsultString($":- mk({n}), drain({n}).");
            Assert.False(e.Query("tok(_).").Success);
            // ANTI-VACUITY on both sides: it compacted, and it compacted few.
            Assert.True(e.ClauseSlotCompactions > 3,
                $"{e.ClauseSlotCompactions} compactions -- tombstoning inert?");
            Assert.True(e.ClauseSlotCompactions < 40,
                $"{e.ClauseSlotCompactions} compactions over {n} retracts");
            // And the chain retired every entry by lookup, none by walking.
            Assert.Equal(n, e.ChainPatchHints);
        }
    }

    /// <summary>A reader BETWEEN retracts, inside the same query, while the
    /// slot still holds tombstones: it must see exactly the live clauses.
    /// This is the compact-on-read seam -- the one place a tombstone could
    /// leak into clause/2, findall or listing if a reader were handed the
    /// physical list.</summary>
    [Fact]
    public void AReaderMidDrainSeesExactlyTheLiveClauses()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(q/1).
            q(1). q(2). q(3). q(4). q(5). q(6). q(7). q(8).
            """);
        // Three retracts: under the halving trigger for 8 clauses at first,
        // so tombstones are present when findall reads.
        Assert.True(e.Query(
            "retract(q(1)), retract(q(3)), retract(q(5)), findall(X, q(X), L),"
            + " L == [2, 4, 6, 7, 8].").Success);
        // clause/2 agrees, after more retracting in the same breath.
        Assert.True(e.Query(
            "retract(q(2)), findall(X, clause(q(X), true), L),"
            + " L == [4, 6, 7, 8].").Success);
    }

    /// <summary>The chain's retired entries and the bypass replay: after
    /// retracts, asserts at BOTH ends, and enough churn to sweep and compact,
    /// dispatch and enumeration still answer in clause order. An entry
    /// bypassed wrongly shows up as a clause that stops answering; a replay
    /// that clobbers a link shows up as one that disappears after an
    /// asserta.</summary>
    [Fact]
    public void ChurnAcrossSweepsKeepsDispatchAndOrderIntact()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(":- dynamic(r/1).");
        for (int round = 0; round < 30; round++)
        {
            Assert.True(e.Query($"assertz(r(z{round})).").Success);
            Assert.True(e.Query($"asserta(r(a{round})).").Success);
            if (round % 2 == 0)
                Assert.True(e.Query($"retract(r(a{round})).").Success);
            if (round % 3 == 0)
                Assert.True(e.Query($"retract(r(z{round})).").Success);
        }
        var got = e.QueryAll("r(X).")
            .Select(s => ((Shumway.Compiler.Ast.AtomTerm)s["X"]!).Name).ToList();
        var expected = Enumerable.Range(0, 30).Reverse()
            .Where(i => i % 2 != 0).Select(i => $"a{i}")
            .Concat(Enumerable.Range(0, 30).Where(i => i % 3 != 0).Select(i => $"z{i}"))
            .ToList();
        Assert.Equal(expected, got);
        // Every survivor still dispatches individually.
        foreach (string v in expected)
            Assert.True(e.Query($"r({v}).").Success, $"r({v}) stopped answering");
        Assert.False(e.Query("r(a0).").Success);
    }

    /// <summary>An append AFTER a retirement, BEFORE the sweep replays the
    /// bypass: the replay reads the retired entry's <c>next</c> slot at
    /// replay time, which is exactly where the append linked in. Getting
    /// that wrong disconnects the appended clause.</summary>
    [Fact]
    public void AnAppendAfterARetirementSurvivesTheSweep()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(":- dynamic(s/1).");
        // Build, retract the TAIL (its bypass stays pending), append more,
        // then churn enough for sweeps to replay.
        Assert.True(e.Query("assertz(s(1)), assertz(s(2)), assertz(s(3)).").Success);
        Assert.True(e.Query("retract(s(3)).").Success);
        Assert.True(e.Query("assertz(s(4)), assertz(s(5)).").Success);
        for (int i = 10; i < 30; i++)
            Assert.True(e.Query($"assertz(s({i})), retract(s({i})).").Success);
        Assert.Equal(new long[] { 1, 2, 4, 5 },
            e.QueryAll("s(X).")
                .Select(s => ((Shumway.Compiler.Ast.IntTerm)s["X"]!).Value));
        Assert.True(e.Query("s(5).").Success);
        Assert.False(e.Query("s(3).").Success);
    }
}
