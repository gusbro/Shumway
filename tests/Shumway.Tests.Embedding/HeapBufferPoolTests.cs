using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Heap-buffer pool — the host recycles a dead activation's heap buffer
/// into the next activation (skipping the doubling ladder for repeated big
/// queries) under a decayed-usage-peak retention policy: repeating big
/// queries keeps the big buffer hot; a spike followed by small queries
/// halves the tracked peak each query until the oversized buffer is
/// dropped (so the process RSS can fall). One slot only — overlapping
/// activations allocate fresh, preserving the suspended-enumeration
/// semantics.
/// </summary>
public class HeapBufferPoolTests
{
    // ~600K cells of live list (2 cells per cons × 300K) — comfortably above
    // the 64K initial heap and the growth is deterministic.
    private const string BigQuery = "length(L, 300000), maplist(=(x), L).";

    [Fact]
    public void BigQuery_BufferIsPooled_AndReused()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(BigQuery).Success);
        long afterBig = e.PooledHeapCapacityCells;
        Assert.True(afterBig >= 600_000,
            $"expected the grown buffer pooled, got {afterBig} cells");
        // The next query adopts the pooled buffer (slot empties during the
        // run) and returns it on death — same capacity, no fresh ladder.
        Assert.True(e.Query(BigQuery).Success);
        Assert.Equal(afterBig, e.PooledHeapCapacityCells);
    }

    [Fact]
    public void DecayedPeak_DropsOversizedBuffer_AfterSmallQueries()
    {
        // Needs a buffer genuinely ABOVE the 1M-cell (8 MB) retention floor:
        // a 3M-element list ≈ 6M live cells → ≥ 8M-cell capacity.
        var e = new PrologEngine();
        Assert.True(e.Query("length(L, 3000000), maplist(=(x), L).").Success);
        long big = e.PooledHeapCapacityCells;
        Assert.True(big >= 6_000_000, $"expected a big pooled buffer, got {big}");
        // Small queries halve the tracked peak each time; once the pooled
        // capacity exceeds 4× the decayed peak it must be dropped.
        for (int i = 0; i < 40 && e.PooledHeapCapacityCells == big; i++)
            Assert.True(e.Query("X is 1 + 1, X == 2.").Success);
        Assert.True(e.PooledHeapCapacityCells < big,
            "oversized buffer was never dropped by the decay policy");
        // Right after the drop the slot is empty; the next ordinary query
        // repools its (small) buffer — the floor keeps the slot warm.
        Assert.True(e.Query("true.").Success);
        Assert.True(e.PooledHeapCapacityCells > 0);
        Assert.True(e.PooledHeapCapacityCells < big);
    }

    [Fact]
    public void SmallBuffers_AlwaysPooled_AboveNothing()
    {
        // Small queries pool their (small) buffer too — the floor keeps the
        // slot warm without churn.
        var e = new PrologEngine();
        Assert.True(e.Query("X = 1.").Success);
        Assert.True(e.PooledHeapCapacityCells > 0);
    }

    [Fact]
    public void OverlappingActivations_BothCorrect_PoolStaysConsistent()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic d/1.
            d(1).
            d(2).
            d(3).
            """);
        // Prime the pool, then open a lazy enumeration (adopts the buffer),
        // run a nested query mid-enumeration (pool empty → fresh alloc),
        // and finish both. Everything stays correct and the pool ends with
        // one buffer.
        Assert.True(e.Query("true.").Success);
        var it = e.QueryAll("d(X).").GetEnumerator();
        Assert.True(it.MoveNext());
        Assert.True(e.Query(BigQuery).Success);          // nested, overlapping
        var seen = new System.Collections.Generic.List<string>
        {
            it.Current["X"]!.ToString()!,
        };
        while (it.MoveNext()) seen.Add(it.Current["X"]!.ToString()!);
        Assert.Equal(new[] { "1", "2", "3" }, seen);
        Assert.True(e.PooledHeapCapacityCells > 0);
    }

    [Fact]
    public void AbandonedEnumeration_StillReturnsBuffer()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic d/1.
            d(1).
            d(2).
            """);
        Assert.True(e.Query(BigQuery).Success);
        long big = e.PooledHeapCapacityCells;
        // foreach with break disposes the enumerator → the activation dies
        // early → its (adopted) buffer returns to the pool.
        foreach (var s in e.QueryAll("d(X)."))
            break;
        Assert.Equal(big, e.PooledHeapCapacityCells);
    }

    [Fact]
    public void FailingAndThrowingQueries_StillReturnBuffers()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("fail.").Success);
        Assert.True(e.PooledHeapCapacityCells > 0);
        long before = e.PooledHeapCapacityCells;
        try { e.Query("X is foo + 1."); }
        catch { }
        Assert.True(e.PooledHeapCapacityCells >= before);
        // And the engine still works normally afterwards.
        Assert.True(e.Query("X = ok, X == ok.").Success);
    }

    [Fact]
    public void ReusedBuffer_QueriesStayCorrect()
    {
        // Semantics with a recycled (stale-content) buffer: results identical
        // across many mixed queries.
        var e = new PrologEngine();
        e.ConsultString("""
            rev([], A, A).
            rev([H|T], A, R) :- rev(T, [H|A], R).
            """);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(e.Query(
                "numlist(1, 2000, L), rev(L, [], R), R = [2000|_], length(R, 2000).").Success);
            Assert.True(e.Query(BigQuery).Success);
            Assert.False(e.Query("numlist(1, 100, L), member(999, L).").Success);
        }
    }
}
