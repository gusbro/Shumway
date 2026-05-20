using Shumway.Compiler.Il;
using Xunit;

namespace Shumway.Tests.Compiler.Il;

/// <summary>
/// Chunk 76 — unit coverage for the PGO counter store. The two-phase
/// PGO pipeline (instrumented IL → optimised IL) routes per-clause hit
/// counts through this process-wide table; these tests pin its
/// allocate / bump / read / release contract independently of the IL
/// emission.
/// </summary>
public class IlProfileCountersTests
{
    // Each test uses a distinct key so the shared static store stays
    // isolated under xUnit's parallel execution.
    private static int _nextTestKey = 1_000_000;
    private static int FreshKey() => System.Threading.Interlocked.Increment(ref _nextTestKey);

    [Fact]
    public void Allocate_ThenGet_ReturnsZeroedArray()
    {
        int key = FreshKey();
        IlProfileCounters.Allocate(key, 4);
        var counts = IlProfileCounters.Get(key);
        Assert.NotNull(counts);
        Assert.Equal(4, counts!.Length);
        Assert.All(counts, c => Assert.Equal(0, c));
        IlProfileCounters.Release(key);
    }

    [Fact]
    public void Bump_IncrementsTheNamedSlot()
    {
        int key = FreshKey();
        IlProfileCounters.Allocate(key, 3);
        IlProfileCounters.Bump(key, 1);
        IlProfileCounters.Bump(key, 1);
        IlProfileCounters.Bump(key, 2);
        var counts = IlProfileCounters.Get(key);
        Assert.Equal(new long[] { 0, 2, 1 }, counts);
        IlProfileCounters.Release(key);
    }

    [Fact]
    public void Bump_OutOfRangeSlot_IsIgnored()
    {
        int key = FreshKey();
        IlProfileCounters.Allocate(key, 2);
        IlProfileCounters.Bump(key, 5);    // out of range — no throw
        IlProfileCounters.Bump(key, -1);   // out of range — no throw
        var counts = IlProfileCounters.Get(key);
        Assert.Equal(new long[] { 0, 0 }, counts);
        IlProfileCounters.Release(key);
    }

    [Fact]
    public void Bump_UnknownKey_IsIgnored()
    {
        // No Allocate — Bump on an unknown key is a silent no-op.
        IlProfileCounters.Bump(FreshKey(), 0);
    }

    [Fact]
    public void TotalSamples_SumsAllSlots()
    {
        int key = FreshKey();
        IlProfileCounters.Allocate(key, 3);
        for (int i = 0; i < 7; i++) IlProfileCounters.Bump(key, 0);
        for (int i = 0; i < 3; i++) IlProfileCounters.Bump(key, 2);
        Assert.Equal(10, IlProfileCounters.TotalSamples(key));
        IlProfileCounters.Release(key);
    }

    [Fact]
    public void TotalSamples_UnknownKey_IsZero()
    {
        Assert.Equal(0, IlProfileCounters.TotalSamples(FreshKey()));
    }

    [Fact]
    public void Release_DropsTheCounters()
    {
        int key = FreshKey();
        IlProfileCounters.Allocate(key, 2);
        IlProfileCounters.Bump(key, 0);
        IlProfileCounters.Release(key);
        Assert.Null(IlProfileCounters.Get(key));
    }

    [Fact]
    public void Get_ReturnsACopy_NotTheLiveArray()
    {
        // Mutating the returned snapshot must not corrupt the store.
        int key = FreshKey();
        IlProfileCounters.Allocate(key, 2);
        IlProfileCounters.Bump(key, 0);
        var snapshot = IlProfileCounters.Get(key)!;
        snapshot[0] = 999;
        Assert.Equal(1, IlProfileCounters.Get(key)![0]);
        IlProfileCounters.Release(key);
    }
}
