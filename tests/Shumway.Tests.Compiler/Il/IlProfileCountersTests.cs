using Shumway.Compiler.Il;
using Xunit;

namespace Shumway.Tests.Compiler.Il;

/// <summary>
/// The PGO counter store. The two-phase PGO pipeline (instrumented IL →
/// optimised IL) routes per-clause hit counts through this process-wide
/// table; these tests pin its allocate / bump / read / release contract
/// independently of the IL emission.
/// </summary>
public class IlProfileCountersTests
{
    // Never handed out: Allocate starts at 1 and the table is far smaller.
    private const int UnknownKey = int.MaxValue;

    [Fact]
    public void Allocate_ThenGet_ReturnsZeroedArray()
    {
        int key = IlProfileCounters.Allocate(4);
        var counts = IlProfileCounters.Get(key);
        Assert.NotNull(counts);
        Assert.Equal(4, counts!.Length);
        Assert.All(counts, c => Assert.Equal(0, c));
        IlProfileCounters.Release(key);
    }

    [Fact]
    public void Bump_IncrementsTheNamedSlot()
    {
        int key = IlProfileCounters.Allocate(3);
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
        int key = IlProfileCounters.Allocate(2);
        IlProfileCounters.Bump(key, 5);    // out of range — no throw
        IlProfileCounters.Bump(key, -1);   // out of range — no throw
        var counts = IlProfileCounters.Get(key);
        Assert.Equal(new long[] { 0, 0 }, counts);
        IlProfileCounters.Release(key);
    }

    [Fact]
    public void Bump_UnknownKey_IsIgnored()
    {
        IlProfileCounters.Bump(UnknownKey, 0);
        IlProfileCounters.Bump(-1, 0);
    }

    [Fact]
    public void TotalSamples_SumsAllSlots()
    {
        int key = IlProfileCounters.Allocate(3);
        for (int i = 0; i < 7; i++) IlProfileCounters.Bump(key, 0);
        for (int i = 0; i < 3; i++) IlProfileCounters.Bump(key, 2);
        Assert.Equal(10, IlProfileCounters.TotalSamples(key));
        IlProfileCounters.Release(key);
    }

    [Fact]
    public void TotalSamples_UnknownKey_IsZero()
    {
        Assert.Equal(0, IlProfileCounters.TotalSamples(UnknownKey));
    }

    [Fact]
    public void Release_DropsTheCounters()
    {
        int key = IlProfileCounters.Allocate(2);
        IlProfileCounters.Bump(key, 0);
        IlProfileCounters.Release(key);
        Assert.Null(IlProfileCounters.Get(key));
        IlProfileCounters.Release(key);   // twice: harmless
    }

    [Fact]
    public void Get_ReturnsACopy_NotTheLiveArray()
    {
        // Mutating the returned snapshot must not corrupt the store.
        int key = IlProfileCounters.Allocate(2);
        IlProfileCounters.Bump(key, 0);
        var snapshot = IlProfileCounters.Get(key)!;
        snapshot[0] = 999;
        Assert.Equal(1, IlProfileCounters.Get(key)![0]);
        IlProfileCounters.Release(key);
    }

    [Fact]
    public void LiveProfiles_HaveTheirOwnCounters_AcrossTableGrowth()
    {
        var keys = new int[200];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = IlProfileCounters.Allocate(1);
            for (int b = 0; b <= i % 5; b++) IlProfileCounters.Bump(keys[i], 0);
        }
        Assert.Equal(keys.Length, keys.Distinct().Count());
        for (int i = 0; i < keys.Length; i++)
            Assert.Equal(i % 5 + 1, IlProfileCounters.TotalSamples(keys[i]));
        foreach (int k in keys) IlProfileCounters.Release(k);
    }

    [Fact]
    public void ALongRunningProcess_ReusesReleasedKeys()
    {
        // A process that keeps recompiling holds as many keys as profiles
        // alive at once, not every profile it ever took.
        for (int i = 0; i < 100_000; i++)
            IlProfileCounters.Release(IlProfileCounters.Allocate(2));
        Assert.True(IlProfileCounters.TableLength < 10_000,
            $"the key table grew to {IlProfileCounters.TableLength} slots");
    }
}
