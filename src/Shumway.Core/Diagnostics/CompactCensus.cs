using System.Diagnostics;

namespace Shumway.Core.Diagnostics;

/// <summary>How often a cut's compaction WALKS the trails versus how often
/// it actually drops anything, and what the walk had to reach to do it.
///
/// <para>The tier's cut steps aside whenever the interpreter would walk,
/// because "was anything trailed since the barrier" is the only question it
/// can answer from linear memory. That is correct and coarser than it needs
/// to be: a walk that drops nothing is a deopt bought for no work.</para>
///
/// <para>The rest says whether the module could do the walk ITSELF, and the
/// four are counted apart because they cost different things. Reading the
/// attribute log is an IMAGE away -- the same technique the functor and
/// attribute tables already use. The three writes are not: the module would
/// have to write managed state back, and a half-done compaction that leaves
/// a record orphaned is a leak this code already documents. So the walks the
/// module could finish are exactly those that read and never write, and
/// these counters are what says how many that is.</para></summary>
public static class CompactCensus
{
    private const string Symbol = "SHUMWAY_DIAG";

    public static long Walks, Dropped;
    /// <summary>Judged an AttrModify entry, which reads the record's home
    /// out of the attribute trail log.</summary>
    public static long ReadTheLog;
    /// <summary>Cleared an orphaned record IN that log.</summary>
    public static long WroteTheLog;
    /// <summary>Dropped a dead record from the attribute STORE, which is a
    /// different table and only happens when the cell stopped being an
    /// attributed variable.</summary>
    public static long DroppedARecord;
    /// <summary>Clipped a catch frame's stale trail snapshot, which is
    /// control state and the third thing a module-side walk would have to
    /// write.</summary>
    public static long ClippedAFrame;
    /// <summary>Walks that read the log and wrote NOTHING: the ones a module
    /// with a read image could finish on its own.</summary>
    public static long ReachableByAnImage;

    [Conditional(Symbol)]
    public static void Reset()
    {
        Walks = 0; Dropped = 0; ReadTheLog = 0; WroteTheLog = 0;
        DroppedARecord = 0; ClippedAFrame = 0; ReachableByAnImage = 0;
        OrphansCleared = 0; AttrPutUpdate = 0; AttrPutInsert = 0;
        System.Array.Clear(AttrDelShape, 0, AttrDelShape.Length);
    }

    /// <summary>Records a module-side compaction orphaned and the host
    /// cleared on the way out. The deferral is only sound if the clearing
    /// actually happens, and nothing else can see that it did.</summary>
    public static long OrphansCleared;

    [Conditional(Symbol)]
    public static void NoteOrphanCleared() => OrphansCleared++;

    /// <summary>How a put_to_attr_list found the store: a row already
    /// there (an UPDATE, which a module can do in place) or not (an
    /// INSERT, which may have to grow an open-addressed table).</summary>
    public static long AttrPutUpdate, AttrPutInsert;

    [Conditional(Symbol)]
    public static void NoteAttrPut(bool update)
    { if (update) AttrPutUpdate++; else AttrPutInsert++; }

    /// <summary>What a del_from_attr_list found: 0 no list at all, 1
    /// nothing matched, 2 the list EMPTIED (a removal), 3 an update.
    /// Only 3 and the two no-ops are things a module can answer.</summary>
    public static readonly long[] AttrDelShape = new long[4];

    [Conditional(Symbol)]
    public static void NoteAttrDel(int shape) => AttrDelShape[shape]++;

    [Conditional(Symbol)]
    public static void NoteWalk() => Walks++;

    [Conditional(Symbol)]
    public static void NoteDropped(bool dropped) { if (dropped) Dropped++; }

    /// <summary>One walk's reach, noted once when it ends.</summary>
    [Conditional(Symbol)]
    public static void NoteReach(bool readLog, bool wroteLog,
                                 bool droppedRecord, bool clippedFrame)
    {
        if (readLog) ReadTheLog++;
        if (wroteLog) WroteTheLog++;
        if (droppedRecord) DroppedARecord++;
        if (clippedFrame) ClippedAFrame++;
        if (!wroteLog && !droppedRecord && !clippedFrame) ReachableByAnImage++;
    }
}
