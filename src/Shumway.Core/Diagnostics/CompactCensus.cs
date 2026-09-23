using System.Diagnostics;

namespace Shumway.Core.Diagnostics;

/// <summary>How often a cut's compaction WALKS the trails versus how often
/// it actually drops anything.
///
/// <para>The tier's cut steps aside whenever the interpreter would walk,
/// because "was anything trailed since the barrier" is the only question it
/// can answer from linear memory. That is correct and coarser than it needs
/// to be: a walk that drops nothing is a deopt bought for no work. This is
/// the ratio that says whether a finer test is worth the mirrors it would
/// cost.</para></summary>
public static class CompactCensus
{
    private const string Symbol = "SHUMWAY_DIAG";

    public static long Walks, Dropped;

    [Conditional(Symbol)]
    public static void Reset() { Walks = 0; Dropped = 0; }

    [Conditional(Symbol)]
    public static void NoteWalk() => Walks++;

    [Conditional(Symbol)]
    public static void NoteDropped(bool dropped) { if (dropped) Dropped++; }
}
