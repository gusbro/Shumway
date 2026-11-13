using System;

namespace Shumway.Core;

/// <summary>ADR-016's adaptive trigger: where the next heap collection
/// happens, decided from the log of the last ones. Everything is in cells,
/// so the decision is deterministic.
///
/// <para>The next threshold is what survived plus a margin. A collection
/// that recovered much of the heap keeps memory tight; one that recovered
/// little spaces the next out, and more so when the one before it recovered
/// little too, since collecting again soon would scan the same live data
/// and the same roots for as little. The margin stays within a multiple of
/// the live data and within half the room left below the ceiling, so as the
/// heap fills the collections come closer together whatever they yield. A
/// large heap also collects before its array grows.</para></summary>
internal static class HeapGcPolicy
{
    /// <summary>A collection that recovered at least this fraction of the
    /// heap paid for itself.</summary>
    public const double ProductiveYield = 0.5;

    /// <summary>Below this fraction a collection hardly paid for itself.</summary>
    public const double PoorYield = 0.2;

    /// <summary>The margin never exceeds this multiple of what survived.</summary>
    public const int MaxLiveMultiple = 8;

    /// <summary>Past this fraction of the ceiling a safe point collects
    /// whatever the margin said.</summary>
    public const double PressureFraction = 0.75;

    /// <summary>A heap still this full of live data after a collection may
    /// grow to the ceiling, where an allocation that does not fit is a
    /// resource error.</summary>
    public const double FullFraction = 0.9;

    /// <summary>How many collections the log keeps.</summary>
    public const int LogSize = 8;

    /// <summary>From an array of this many cells (16 MB) on, the heap
    /// collects before the array grows.</summary>
    public const long CollectBeforeGrowingCells = 1 << 21;

    /// <summary>One collection: the heap top before it, what survived, the
    /// roots it scanned (stack and trails), and the margin it set.</summary>
    public readonly record struct Entry(long HeapBefore, long Live, long Roots, long Margin)
    {
        /// <summary>The fraction of the heap the collection recovered.</summary>
        public double Yield => HeapBefore > 0 ? (double)(HeapBefore - Live) / HeapBefore : 0;
    }

    /// <summary>The margin past <paramref name="last"/>'s live cells before
    /// the next collection. The band is the average yield of the last two
    /// collections, so one odd collection does not swing it.</summary>
    public static long Margin(Entry last, Entry? previous, long floor)
    {
        double yield = previous is { } p ? (last.Yield + p.Yield) / 2 : last.Yield;
        long live = last.Live;
        long margin;
        if (yield >= ProductiveYield)
            margin = Math.Max(live, floor);
        else if (yield >= PoorYield)
            margin = Math.Max(2 * live, floor);
        else
        {
            // The roots weigh only here: a collection that recovers much is
            // worth its scan, one that recovers little is not.
            margin = Math.Max(Math.Max(2 * live, last.Roots), floor);
            if (previous is { } q && q.Yield < PoorYield)
                margin = Math.Max(margin, 2 * q.Margin);
        }
        return Math.Min(margin, Math.Max((long)MaxLiveMultiple * live, floor));
    }

    /// <summary>The heap top at which the next collection triggers. Always
    /// past <paramref name="live"/>: a threshold at or below it would collect
    /// at every safe point.</summary>
    public static long NextThreshold(long live, long margin, long ceiling)
    {
        // Nearly full: a configured maximum lets the heap grow to it, where an
        // allocation that does not fit is the resource error. A ceiling the
        // runtime estimated can be passed; past it the heap collects each
        // time it grows by half its live data, at most the margin.
        if (live >= FullFraction * ceiling)
            return Math.Max(ceiling, live + Math.Max(1, Math.Min(margin, live / 2)));
        long next = live + Math.Min(margin, (ceiling - live) / 2);
        long pressure = (long)(PressureFraction * ceiling);
        if (live < pressure) next = Math.Min(next, pressure);
        return Math.Max(next, live + 1);
    }

    /// <summary>The highest threshold the heap array allows. From
    /// <see cref="CollectBeforeGrowingCells"/> on, the next collection comes
    /// before the array is full, so the array grows only when a collection
    /// there cannot leave room for the live data (at least the floor);
    /// otherwise the limit is the same point of the array doubled once.</summary>
    public static long ArrayLimit(long live, long capacity, long floor)
    {
        if (capacity < CollectBeforeGrowingCells) return long.MaxValue;
        // The array grows inside an allocation, before the safe point that
        // would collect sees the heap past the threshold: an eighth of slack
        // keeps the collection ahead of it.
        long usable = capacity - capacity / 8;
        return usable - live >= Math.Max(live, floor) ? usable : 2 * usable;
    }

    /// <summary>The heap's ceiling in cells: the configured maximum, or half
    /// the memory the runtime reports available to the process (which
    /// honours a container's limit).</summary>
    public static long Ceiling(int maxHeapCells)
    {
        if (maxHeapCells > 0) return maxHeapCells;
        long bytes = AvailableBytes();
        return Math.Min(bytes / 2 / 8, int.MaxValue);
    }

    private static long AvailableBytes()
    {
#if NETFRAMEWORK
        return Environment.Is64BitProcess ? 16L << 30 : 1L << 30;
#else
        try
        {
            long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return bytes > 0 ? bytes : 16L << 30;
        }
        catch (PlatformNotSupportedException)
        {
            return 16L << 30;
        }
#endif
    }
}
