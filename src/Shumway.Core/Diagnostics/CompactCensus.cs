using System.Diagnostics;

namespace Shumway.Core.Diagnostics;

/// <summary>How often a cut's compaction WALKS the trails versus how often
/// it actually drops anything, and what the walk had to touch.
///
/// <para>The tier's cut steps aside whenever the interpreter would walk,
/// because "was anything trailed since the barrier" is the only question it
/// can answer from linear memory. That is correct and coarser than it needs
/// to be: a walk that drops nothing is a deopt bought for no work.</para>
///
/// <para>The rest says whether the module could do the walk ITSELF. Two
/// things in it reach managed state the module has no image of: an
/// AttrModify entry is judged by its record's HOME, which lives in the
/// attribute trail log, and a DROPPED entry writes that log back (an
/// orphaned record, or a dead attribute record). A walk that touches
/// neither needs only the trails, which are already shared, plus the
/// catch-frame floor, which is one number. This counts those separately so
/// the split decides the design instead of a guess.</para></summary>
public static class CompactCensus
{
    private const string Symbol = "SHUMWAY_DIAG";

    public static long Walks, Dropped, SawAttrModify, WroteTheLog;

    [Conditional(Symbol)]
    public static void Reset()
    { Walks = 0; Dropped = 0; SawAttrModify = 0; WroteTheLog = 0; }

    [Conditional(Symbol)]
    public static void NoteWalk() => Walks++;

    [Conditional(Symbol)]
    public static void NoteDropped(bool dropped) { if (dropped) Dropped++; }

    /// <summary>One walk's reach, noted once per walk when it ends.</summary>
    [Conditional(Symbol)]
    public static void NoteReach(bool sawAttrModify, bool wroteTheLog)
    {
        if (sawAttrModify) SawAttrModify++;
        if (wroteTheLog) WroteTheLog++;
    }
}
