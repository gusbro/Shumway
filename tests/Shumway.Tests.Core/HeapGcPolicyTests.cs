using System;
using System.Collections.Generic;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>ADR-016's adaptive trigger as a pure rule: a log of collections
/// in, the next threshold out. All sizes are in cells.</summary>
public class HeapGcPolicyTests
{
    private const long MB = 1 << 17;          // cells per megabyte
    private const long Floor = 1 << 20;       // ActivationConfig.GcThreshold's default
    private const long Ceiling8GB = 8192 * MB;

    private static long Next(HeapGcPolicy.Entry last, HeapGcPolicy.Entry? previous, long ceiling, long floor = Floor)
        => HeapGcPolicy.NextThreshold(last.Live, HeapGcPolicy.Margin(last, previous, floor), ceiling);

    [Fact]
    public void ACollectionThatRecoveredThreeQuartersKeepsTheNextOneAtTwiceTheLiveData()
    {
        // A 1 GB heap that gave back 768 MB: the next collection is at 512 MB, not 1 GB.
        var last = new HeapGcPolicy.Entry(1024 * MB, 256 * MB, Roots: 1000, Margin: 0);
        Assert.Equal(512 * MB, Next(last, null, Ceiling8GB));
    }

    [Fact]
    public void ACollectionThatRecoveredATenthSpacesTheNextOneToThreeTimesTheLiveData()
    {
        // A 1 GB heap that gave back 100 MB: the next collection is at 2.7 GB.
        var last = new HeapGcPolicy.Entry(1024 * MB, 924 * MB, Roots: 1000, Margin: 0);
        Assert.Equal(3 * 924 * MB, Next(last, null, Ceiling8GB));
    }

    [Theory]
    [InlineData(500, 500)]     // recovered exactly half: productive, margin L
    [InlineData(501, 1002)]    // just under half: the middle band, margin 2L
    [InlineData(800, 1600)]    // recovered exactly a fifth: still the middle band
    [InlineData(801, 1602)]    // just under a fifth: poor, margin 2L (no roots, no history)
    public void TheBandEdgesBelongToTheBetterBand(long live, long margin)
    {
        var last = new HeapGcPolicy.Entry(1000, live, Roots: 0, Margin: 0);
        Assert.Equal(margin, HeapGcPolicy.Margin(last, null, floor: 64));
    }

    [Fact]
    public void TheRootsSpaceOutOnlyACollectionThatRecoveredLittle()
    {
        var productive = new HeapGcPolicy.Entry(1000, 100, Roots: 100_000, Margin: 0);
        Assert.Equal(100, HeapGcPolicy.Margin(productive, null, floor: 64));

        var middle = new HeapGcPolicy.Entry(1000, 700, Roots: 3000, Margin: 0);
        Assert.Equal(1400, HeapGcPolicy.Margin(middle, null, floor: 64));

        var poor = new HeapGcPolicy.Entry(1000, 900, Roots: 3000, Margin: 0);
        Assert.Equal(3000, HeapGcPolicy.Margin(poor, null, floor: 64));
    }

    [Fact]
    public void TwoPoorCollectionsInARowDoubleTheMargin()
    {
        var previous = new HeapGcPolicy.Entry(500, 450, Roots: 0, Margin: 1500);
        var last = new HeapGcPolicy.Entry(1000, 900, Roots: 0, Margin: 0);
        Assert.Equal(3000, HeapGcPolicy.Margin(last, previous, floor: 64));
    }

    [Fact]
    public void TheBandIsTheAverageOfTheLastTwoCollections()
    {
        // 10% after 60%: an average of 35% is the middle band, not the poor one,
        // and the previous margin is not doubled.
        var previous = new HeapGcPolicy.Entry(1000, 400, Roots: 0, Margin: 5000);
        var last = new HeapGcPolicy.Entry(1000, 900, Roots: 100_000, Margin: 0);
        Assert.Equal(1800, HeapGcPolicy.Margin(last, previous, floor: 64));

        // 60% after 10%: productive on its own, the middle band on average.
        var poorBefore = new HeapGcPolicy.Entry(1000, 900, Roots: 0, Margin: 5000);
        var recovered = new HeapGcPolicy.Entry(1000, 400, Roots: 0, Margin: 0);
        Assert.Equal(800, HeapGcPolicy.Margin(recovered, poorBefore, floor: 64));
    }

    [Fact]
    public void TheMarginNeverPassesEightTimesTheLiveData()
    {
        var roots = new HeapGcPolicy.Entry(1000, 900, Roots: 100_000, Margin: 0);
        Assert.Equal(7200, HeapGcPolicy.Margin(roots, null, floor: 64));

        var previous = new HeapGcPolicy.Entry(1000, 900, Roots: 0, Margin: 50_000);
        var backoff = new HeapGcPolicy.Entry(1000, 950, Roots: 0, Margin: 0);
        Assert.Equal(7600, HeapGcPolicy.Margin(backoff, previous, floor: 64));
    }

    [Fact]
    public void TheMarginNeverDropsBelowTheFloor()
    {
        var tiny = new HeapGcPolicy.Entry(100, 10, Roots: 0, Margin: 0);
        Assert.Equal(1000, HeapGcPolicy.Margin(tiny, null, floor: 1000));
    }

    [Fact]
    public void TheMarginTakesAtMostHalfTheRoomLeftBelowTheCeiling()
    {
        Assert.Equal(550, HeapGcPolicy.NextThreshold(live: 100, margin: 800, ceiling: 1000));
        Assert.Equal(300, HeapGcPolicy.NextThreshold(live: 100, margin: 200, ceiling: 1000));
    }

    [Fact]
    public void BelowThreeQuartersOfTheCeilingTheNextCollectionIsNoLaterThanThreeQuarters()
    {
        Assert.Equal(750, HeapGcPolicy.NextThreshold(live: 600, margin: 1000, ceiling: 1000));
        // Past three quarters the room left decides alone.
        Assert.Equal(900, HeapGcPolicy.NextThreshold(live: 800, margin: 1000, ceiling: 1000));
    }

    [Fact]
    public void WhileTheLiveDataIsBelowNinetyPercentACollectionComesBeforeTheCeiling()
    {
        const long ceiling = 1000;
        long previousGap = long.MaxValue;
        for (long live = 0; live < 900; live += 25)
        {
            long next = HeapGcPolicy.NextThreshold(live, margin: 1_000_000, ceiling);
            Assert.True(next > live, $"live {live}: next {next} would collect at every safe point");
            Assert.True(next < ceiling, $"live {live}: next {next} is not before the ceiling");
            // Past the pressure point the collections come closer together.
            if (live >= 750)
            {
                Assert.True(next - live <= previousGap, $"live {live}: gap {next - live} grew");
                previousGap = next - live;
            }
        }
    }

    [Fact]
    public void ANearlyFullHeapGrowsToAConfiguredCeiling()
    {
        Assert.Equal(1000, HeapGcPolicy.NextThreshold(live: 950, margin: 10, ceiling: 1000));
        Assert.True(HeapGcPolicy.NextThreshold(live: 900, margin: 1_000_000, ceiling: 1000) >= 1000);
    }

    [Fact]
    public void PastAnEstimatedCeilingTheHeapCollectsEachTimeItGrowsByHalfItsLiveData()
    {
        Assert.Equal(6000, HeapGcPolicy.NextThreshold(live: 4000, margin: 10_000, ceiling: 1000));
        Assert.Equal(4100, HeapGcPolicy.NextThreshold(live: 4000, margin: 100, ceiling: 1000));
    }

    [Fact]
    public void TheNextThresholdIsAlwaysPastTheLiveData()
    {
        Assert.Equal(9, HeapGcPolicy.NextThreshold(live: 8, margin: 0, ceiling: 10));
        Assert.Equal(1, HeapGcPolicy.NextThreshold(live: 0, margin: 0, ceiling: 1));
        Assert.Equal(11, HeapGcPolicy.NextThreshold(live: 10, margin: 0, ceiling: 10));
    }

    [Fact]
    public void ASmallArrayDoesNotLimitTheThreshold()
    {
        long capacity = HeapGcPolicy.CollectBeforeGrowingCells / 2;
        Assert.Equal(long.MaxValue, HeapGcPolicy.ArrayLimit(live: 100, capacity, floor: 64));
    }

    [Fact]
    public void ALargeArrayIsCollectedBeforeItGrowsWhenThatLeavesRoomForTheLiveData()
    {
        const long capacity = 1 << 21;
        const long usable = capacity - capacity / 8;          // 1,835,008
        Assert.Equal(usable, HeapGcPolicy.ArrayLimit(live: 800_000, capacity, floor: 64));
        // Room for less than the live data: the array grows, once.
        Assert.Equal(2 * usable, HeapGcPolicy.ArrayLimit(live: 1_000_000, capacity, floor: 64));
        // The floor counts as live data here.
        Assert.Equal(2 * usable, HeapGcPolicy.ArrayLimit(live: 100, capacity, floor: 1_900_000));
    }

    [Fact]
    public void TheCeilingIsTheConfiguredMaximumWhenThereIsOne()
    {
        Assert.Equal(12345, HeapGcPolicy.Ceiling(12345));
        long estimated = HeapGcPolicy.Ceiling(0);
        Assert.InRange(estimated, 1, int.MaxValue);
    }
}

/// <summary>ADR-016's adaptive trigger in an activation: a heap built
/// directly, live data rooted in a register and garbage beside it, the
/// safe point that collects, and the threshold it leaves.</summary>
public class HeapGcTriggerTests
{
    private static Activation NewActivation(int floor, int maxHeap, int initialHeap = 1024)
    {
        var e = new Activation(new ActivationConfig
        {
            GcThreshold = floor,
            MaxHeapSize = maxHeap,
            InitialHeapSize = initialHeap,
        });
        // Each register starts as Ref(0), which would pin heap cell 0; the
        // list rooted in register 0 is the only live data.
        for (int i = 0; i < e.RegisterCount; i++) e.SetRegister(i, Cell.Atom(0));
        e.SetRegister(0, Cell.Atom(AtomTable.EmptyListId));
        return e;
    }

    // `cells` live cells: list pairs prepended to the list in register 0.
    private static void AddLive(Activation e, int cells)
    {
        for (int i = 0; i < cells / 2; i++)
        {
            int p = e.AllocateHeap(2);
            e.SetHeap(p, Cell.Int(i));
            e.SetHeap(p + 1, e.GetRegister(0));
            e.SetRegister(0, Cell.Lis(p));
        }
    }

    private static void AddGarbage(Activation e, int cells)
    {
        for (int i = 0; i < cells; i++) e.AllocateHeapUnbound();
    }

    [Fact]
    public void AProductiveCollectionReArmsAtTwiceTheLiveData()
    {
        var e = NewActivation(floor: 64, maxHeap: 1 << 20);
        AddLive(e, 200);
        AddGarbage(e, 800);

        e.MaybeCollectHeap();

        Assert.Equal(1, e.HeapGcRuns);
        var log = e.GcLog();
        Assert.Single(log);
        Assert.Equal(1000, log[0].HeapBefore);
        Assert.Equal(200, log[0].Live);
        Assert.Equal(200, e.HeapTop);
        Assert.Equal(400, e.GcThresholdCells);
        Assert.Equal(200, log[0].Margin);
    }

    [Fact]
    public void APoorCollectionReArmsAtThreeTimesTheLiveData()
    {
        var e = NewActivation(floor: 64, maxHeap: 1 << 20);
        AddLive(e, 900);
        AddGarbage(e, 100);

        e.MaybeCollectHeap();

        Assert.Equal(900, e.HeapTop);
        Assert.Equal(2700, e.GcThresholdCells);
    }

    [Fact]
    public void BelowThreeQuartersOfTheMaximumTheNextCollectionIsAtThreeQuarters()
    {
        var e = NewActivation(floor: 64, maxHeap: 10_000);
        AddLive(e, 6000);
        AddGarbage(e, 3000);

        e.MaybeCollectHeap();

        Assert.Equal(6000, e.HeapTop);
        Assert.Equal(7500, e.GcThresholdCells);
    }

    /// <summary>A collection that recovered 40% would space the next one to
    /// three times the live data, past a 16 MB array; the next one comes
    /// instead at seven eighths of the array, which then does not grow.</summary>
    [Fact]
    public void ALargeArrayIsCollectedBeforeItGrows()
    {
        const int capacity = 1 << 21;
        var e = NewActivation(floor: 64, maxHeap: 1 << 24, initialHeap: capacity);
        AddLive(e, 900_000);
        AddGarbage(e, 600_000);

        e.MaybeCollectHeap();

        Assert.Equal(900_000, e.HeapTop);
        Assert.Equal(capacity - capacity / 8, e.GcThresholdCells);
        while (e.HeapGcRuns < 2)
        {
            AddGarbage(e, 1000);
            e.MaybeCollectHeap();
        }
        Assert.Equal(capacity, e.HeapCapacity);
    }

    /// <summary>A poor collection that leaves less room than the live data
    /// in a 16 MB array lets it grow, but only once: the next collection is at
    /// seven eighths of the doubled array, not at three times the live data.</summary>
    [Fact]
    public void ALargeArrayWithoutRoomForTheLiveDataGrowsOnce()
    {
        const int capacity = 1 << 21;
        var e = NewActivation(floor: 64, maxHeap: 1 << 24, initialHeap: capacity);
        AddLive(e, 1_500_000);
        AddGarbage(e, 100_000);

        e.MaybeCollectHeap();

        Assert.Equal(1_500_000, e.HeapTop);
        Assert.Equal(2 * (capacity - capacity / 8), e.GcThresholdCells);
    }

    [Fact]
    public void ACollectionThatDidNotRunTriesAgainWhenTheHeapDoubles()
    {
        var e = NewActivation(floor: 64, maxHeap: 1 << 20);
        AddGarbage(e, 1000);
        e.EnterGcInhibit();
        try
        {
            e.MaybeCollectHeap();
        }
        finally
        {
            e.ExitGcInhibit();
        }

        Assert.Equal(0, e.HeapGcRuns);
        Assert.Empty(e.GcLog());
        Assert.Equal(2000, e.GcThresholdCells);
    }

    [Fact]
    public void TheLogKeepsTheLastEightCollectionsOldestFirst()
    {
        var e = NewActivation(floor: 64, maxHeap: 1 << 20);
        int collections = 0;
        while (collections < 11)
        {
            AddLive(e, 20);
            AddGarbage(e, 20);
            int runs = e.HeapGcRuns;
            e.MaybeCollectHeap();
            if (e.HeapGcRuns != runs)
            {
                collections++;
                Assert.Equal(e.HeapTop, e.GcLog()[^1].Live);
            }
        }

        var log = e.GcLog();
        Assert.Equal(HeapGcPolicy.LogSize, log.Length);
        for (int i = 1; i < log.Length; i++)
            Assert.True(log[i].Live > log[i - 1].Live, $"entry {i} is not newer than entry {i - 1}");
    }

    /// <summary>Live data that keeps growing toward a configured maximum,
    /// with garbage beside it: the collections come before the maximum while
    /// the live data is under 90% of it, so the program gets to use the
    /// memory, and the end is the resource error, not a heap that filled
    /// with garbage the collector never got to.</summary>
    [Fact]
    public void LiveDataGrowingTowardTheMaximumIsCollectedUntilNinetyPercentThenTheResourceError()
    {
        const int max = 10_000;
        var e = NewActivation(floor: 64, maxHeap: max);
        var thresholds = new List<(long Live, int Threshold)>();
        PrologRuntimeException? exhausted = null;
        try
        {
            while (true)
            {
                AddLive(e, 2);
                AddGarbage(e, 6);
                int runs = e.HeapGcRuns;
                e.MaybeCollectHeap();
                if (e.HeapGcRuns != runs) thresholds.Add((e.HeapTop, e.GcThresholdCells));
            }
        }
        catch (PrologRuntimeException ex)
        {
            exhausted = ex;
        }

        Assert.NotNull(exhausted);
        Assert.Equal("resource_error", exhausted!.Kind);
        Assert.Equal("memory", exhausted.Detail);
        Assert.True(thresholds.Count > 10, $"only {thresholds.Count} collections");
        foreach (var (live, threshold) in thresholds)
        {
            if (live < 0.9 * max)
                Assert.True(threshold < max,
                    $"live {live}: threshold {threshold} let the heap reach the maximum first");
            Assert.True(threshold > live, $"live {live}: threshold {threshold}");
        }
        Assert.True(thresholds[^1].Live >= 0.9 * max,
            $"the last collection left {thresholds[^1].Live} live: the heap filled before 90%");
    }
}
