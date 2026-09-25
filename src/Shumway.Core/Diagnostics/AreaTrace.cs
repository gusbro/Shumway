using System.Diagnostics;

namespace Shumway.Core.Diagnostics;

/// <summary>Samples of how big the WAM areas are, taken on both tiers at
/// points both of them reach, so the two can be laid side by side and asked
/// WHERE they start to differ.
///
/// <para>The index is <see cref="Activation.CellsAllocated"/>, not time and
/// not a sample number. It is monotonic, it is maintained on both paths (the
/// wasm tier folds its own claim counter back in at every crossing), and it
/// is already this project's deterministic metric -- so sample k of one
/// trace and sample k of the other describe the same amount of work done,
/// which is the only thing that makes a comparison mean anything. Two traces
/// indexed by wall clock or by sample count would drift apart for reasons
/// that have nothing to do with the bug.</para>
///
/// <para>Bounded and lossy on purpose: a run that has to be traced is one
/// that does not terminate, so an unbounded log would exhaust the memory the
/// trace exists to explain. Once the ring is full it keeps every Nth sample
/// instead, halving the resolution each time it fills, which keeps the SHAPE
/// of the whole run rather than a close-up of its beginning.</para>
///
/// <para>Everything here is <see cref="ConditionalAttribute"/> on
/// SHUMWAY_DIAG, so a normal build carries no call sites at all -- the same
/// rule the tier's other counters follow.</para></summary>
public static class AreaTrace
{
    private const string Symbol = "SHUMWAY_DIAG";
    private const int Capacity = 4096;

    private static readonly long[] Cells = new long[Capacity];
    private static readonly int[] StackTops = new int[Capacity];
    private static readonly int[] Bs = new int[Capacity];
    private static readonly int[] HeapTops = new int[Capacity];
    private static int _count;
    // Keep one sample in every _stride; doubles whenever the ring fills.
    private static int _stride = 1;
    private static long _seen;

    /// <summary>Off by default: a trace is asked for, never left running.
    /// </summary>
    public static bool Enabled;

    [Conditional(Symbol)]
    public static void Reset()
    {
        _count = 0;
        _stride = 1;
        _seen = 0;
    }

    [Conditional(Symbol)]
    public static void Note(long cells, int stackTop, int b, int heapTop)
    {
        if (!Enabled) return;
        if (_seen++ % _stride != 0) return;
        if (_count == Capacity)
        {
            // Full: halve the resolution in place, keeping the even samples,
            // and carry on. The run's shape survives; its detail does not.
            for (int i = 0; i < Capacity / 2; i++)
            {
                Cells[i] = Cells[i * 2];
                StackTops[i] = StackTops[i * 2];
                Bs[i] = Bs[i * 2];
                HeapTops[i] = HeapTops[i * 2];
            }
            _count = Capacity / 2;
            _stride *= 2;
        }
        Cells[_count] = cells;
        StackTops[_count] = stackTop;
        Bs[_count] = b;
        HeapTops[_count] = heapTop;
        _count++;
    }

    /// <summary>The samples as lines of "cells stackTop B heapTop", oldest
    /// first. Plain numbers, one sample per line: the point is to write two
    /// of these to files and diff them.</summary>
    public static string Dump()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# cells stackTop B heapTop (stride ").Append(_stride)
          .Append(", ").Append(_count).Append(" samples)\n");
        for (int i = 0; i < _count; i++)
            sb.Append(Cells[i]).Append(' ').Append(StackTops[i]).Append(' ')
              .Append(Bs[i]).Append(' ').Append(HeapTops[i]).Append('\n');
        return sb.ToString();
    }
}
