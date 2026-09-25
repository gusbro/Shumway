namespace Shumway.Core;

/// <summary>The attribute trail log's HOME column in a form a compiled wasm
/// module can read: one i32 per record, dense, indexed by log index.
///
/// <para>Why an image at all: a cut's compaction judges an AttrModify entry
/// by the RECORD's home and not by anything in the entry itself, and the
/// records live in a managed list. That single read is why the tier's cut
/// hands the whole walk back to the host. Measured on clp(Z)'s struct+repeat
/// stage, 717 of 784 walks read it, and 479 of them read it and write
/// nothing at all -- those are the walks a module with this image can
/// finish on its own.</para>
///
/// <para>The host writes, the module only reads, so the list stays the one
/// source of truth and the image is a pure derivation. There are exactly
/// four writers of the list and each one calls through here: the append in
/// TrailAttrChange, the truncation an AttrModify unwind does, the orphan
/// clearing a cut's compaction does, and the heap GC's relocation. <see
/// cref="AttrLogMirrorVerify"/> asserts the derivation, which is what keeps
/// a fifth writer from being added in silence.</para>
///
/// <para>A cleared record's home is int.MinValue, which is below every floor
/// and so survives every test. That is the same answer the list gives, and
/// deliberately so: the image does not get to be cleverer than what it
/// mirrors.</para>
///
/// <para>Off until something asks for it, because an engine with no wasm
/// world should not pay for attribute mutation.</para></summary>
public sealed partial class Activation
{
    private int[]? _attrLogHomes;

    internal bool AttrLogMirrorEnabled => _attrLogHomes is not null;

    /// <summary>Starts mirroring, building the image from the log as it
    /// stands. Idempotent.</summary>
    public void AttrLogMirrorEnable()
    {
        if (_attrLogHomes is not null) return;
        _attrLogHomes = new int[System.Math.Max(64, _attrTrailLog.Count * 2)];
        for (int i = 0; i < _attrTrailLog.Count; i++)
            _attrLogHomes[i] = _attrTrailLog[i].Home;
    }

    /// <summary>The homes, for the world that stages them into linear memory.
    /// The array is REPLACED on growth, so a caller re-reads it per chain
    /// rather than caching it.</summary>
    public int[] AttrLogHomes => _attrLogHomes ?? System.Array.Empty<int>();

    /// <summary>How many of them are live. The array is longer.</summary>
    public int AttrLogCount => _attrTrailLog.Count;

    private void AttrLogMirrorAppend(int logIndex, int home)
    {
        if (_attrLogHomes is null) return;
        if (logIndex >= _attrLogHomes.Length)
        {
            int n = _attrLogHomes.Length;
            while (n <= logIndex) n *= 2;
            System.Array.Resize(ref _attrLogHomes, n);
        }
        _attrLogHomes[logIndex] = home;
    }

    /// <summary>A record's home changed in place: the compaction orphaned it,
    /// or the GC moved it.</summary>
    private void AttrLogMirrorSet(int logIndex, int home)
    {
        if (_attrLogHomes is null) return;
        if ((uint)logIndex < (uint)_attrLogHomes.Length)
            _attrLogHomes[logIndex] = home;
    }

    /// <summary>Asserts the image at every staging, which is what makes a
    /// forgotten writer a loud failure instead of a module reading a stale
    /// home. Diagnostic builds only: it is O(log), and the whole point of
    /// the funnel is that release builds do not need to check.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    public void AttrLogMirrorAssert()
    {
        if (AttrLogMirrorVerify() is { } bad)
            throw new System.InvalidOperationException(
                "the attribute log image diverged from the log: " + bad);
    }

    /// <summary>Asserts the image still says what the log says. For tests:
    /// the image is only sound while every writer goes through the funnel,
    /// and this is what makes a forgotten one a failure rather than a
    /// module reading a stale home.</summary>
    public string? AttrLogMirrorVerify()
    {
        if (_attrLogHomes is null) return null;
        for (int i = 0; i < _attrTrailLog.Count; i++)
            if (_attrLogHomes[i] != _attrTrailLog[i].Home)
                return $"attr log image row {i}: image {_attrLogHomes[i]},"
                     + $" log {_attrTrailLog[i].Home}";
        return null;
    }
}
