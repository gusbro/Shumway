namespace Shumway.Core;

/// <summary>The attribute store's image in a form a compiled wasm module can
/// read: a flat open-addressed table of (home, module) -&gt; value, all of it
/// heap INDICES, kept by the five writers in Activation.Attrs.cs and by
/// nothing else.
///
/// <para>Why an image at all: the store is a Dictionary of Dictionaries, which
/// is managed state a module cannot reach, so every get_attr/3 has to leave
/// the module and come back. In clpr that is 12,600 exits, the single largest
/// source of them, and only 1,200 of those fail -- so open-coding the failing
/// path alone would buy nothing, and the module has to be able to READ the
/// store to get rid of the rest.</para>
///
/// <para>The host writes, the module only reads. That keeps the store the one
/// source of truth and the image a pure derivation, so a divergence can only
/// run one way and <see cref="AttrMirrorVerify"/> can assert it. A module
/// reading a stale row would be unsound, which is the whole reason the funnel
/// came first: there is no seventh writer left to forget.</para>
///
/// <para>Row layout, two i64 per slot: key ((home + 1) &lt;&lt; 32) |
/// (uint)module, then value. Key 0 is EMPTY and stops a probe; key -1 is a
/// tombstone and does not. The +1 is what keeps home 0, a real heap index,
/// distinguishable from an empty slot.</para>
///
/// <para>Off until something asks for it: an engine with no wasm world pays
/// nothing, which matters because put_attr/3 is how every solver posts.</para>
/// </summary>
public sealed partial class Activation
{
    private long[]? _attrMirror;
    private int _attrMirrorMask;
    /// <summary>Slots in use, tombstones included: what the load factor is
    /// measured against, since a tombstone still lengthens a probe.</summary>
    private int _attrMirrorUsed;

    private const long AttrMirrorEmpty = 0;
    private const long AttrMirrorTomb = -1;

    internal bool AttrMirrorEnabled => _attrMirror is not null;

    /// <summary>Starts mirroring, building the image from the store as it
    /// stands. Idempotent.</summary>
    public void AttrMirrorEnable()
    {
        if (_attrMirror is null) AttrMirrorRebuild(64);
    }

    /// <summary>The rows, for the world that stages them into linear memory.
    /// The array is REPLACED on growth, so a caller re-reads it per chain
    /// rather than caching it.</summary>
    public long[] AttrMirrorRows => _attrMirror ?? System.Array.Empty<long>();

    public int AttrMirrorMask => _attrMirrorMask;

    /// <summary>How many rows a module may insert before the load factor
    /// would want a rebuild. Capped, because a module that inserts a
    /// great many rows without ever coming out is not a case worth
    /// optimising for, and a small reserve keeps the arithmetic obvious.
    /// </summary>
    public int AttrMirrorInsertBudget
    {
        get
        {
            if (_attrMirror is null) return 0;
            // AttrMirrorPut rebuilds at used * 4 >= Length, so this is
            // the distance to that line, read from the same expression.
            int room = (_attrMirror.Length / 4) - _attrMirrorUsed;
            if (room <= 0) return 0;
            return room < 16 ? room : 16;
        }
    }

    /// <summary>Counts a row a MODULE inserted into the image. The host
    /// never sees the insert -- its own AttrMirrorPut finds the key
    /// already there and only updates the value -- so the occupancy has
    /// to be told, or the table drifts past its load factor and never
    /// rebuilds. Only a FRESH slot counts, exactly as in AttrMirrorPut.
    /// </summary>
    internal void AttrMirrorNoteModuleInsert() => _attrMirrorUsed++;

    /// <summary>The probe's starting slot. Multiply, add, xor, shift: each
    /// step is one wasm instruction, because the module recomputes this exact
    /// function on the other side. Changing it means changing both.</summary>
    private static int AttrMirrorHash(int home, int moduleId, int mask)
    {
        uint h = (uint)home * 2654435761u + (uint)moduleId * 2246822519u;
        h ^= h >> 15;
        return (int)(h & (uint)mask);
    }

    private static long AttrMirrorKey(int home, int moduleId)
        => ((long)(home + 1) << 32) | (uint)moduleId;

    /// <summary>A module id no module has, carrying a variable's ROW COUNT
    /// in a row of its own.
    ///
    /// <para>Why the count is in the image at all: a module can add a row
    /// and take one away, but it cannot CREATE or DESTROY the record --
    /// promoting a plain variable to an attributed one, and demoting it
    /// back when its last attribute goes. Both turn on how many rows the
    /// variable has, which is a managed dictionary's Count, and that one
    /// number is the whole reason those two operations stayed the host's
    /// (193 of the 630 crossings clp(Z) had left).</para>
    ///
    /// <para>In the SAME table rather than a second one, because the funnel
    /// is what makes a derived view trustworthy and there is no sense in
    /// having two of them to keep. Module ids are small and positive, so -1
    /// cannot collide with one.</para></summary>
    internal const int AttrMirrorCountModule = -1;

    /// <summary>The count row for a home, or 0. Reading it is the same
    /// probe the module runs, so the two cannot disagree about a home.
    /// </summary>
    internal int AttrMirrorRowCount(int home)
    {
        if (_attrMirror is null) return 0;
        long key = AttrMirrorKey(home, AttrMirrorCountModule);
        long[] rows = _attrMirror;
        int slot = AttrMirrorHash(home, AttrMirrorCountModule, _attrMirrorMask);
        for (int n = _attrMirrorMask + 1; n > 0; n--)
        {
            long k = rows[slot * 2];
            if (k == AttrMirrorEmpty) return 0;
            if (k == key) return (int)rows[slot * 2 + 1];
            slot = (slot + 1) & _attrMirrorMask;
        }
        return 0;
    }

    private void AttrMirrorSetRowCount(int home, int count)
    {
        if (_attrMirror is null) return;
        if (count <= 0) AttrMirrorDelete(home, AttrMirrorCountModule);
        else AttrMirrorPut(home, AttrMirrorCountModule, count);
    }

    private void AttrMirrorBumpRowCount(int home, int delta)
        => AttrMirrorSetRowCount(home, AttrMirrorRowCount(home) + delta);

    private void AttrMirrorPut(int home, int moduleId, int value)
    {
        if (_attrMirror is null) return;
        if (_attrMirrorUsed * 4 >= _attrMirror.Length)
        {
            // Rebuilding rather than growing in place is also what sweeps the
            // tombstones, which is the only thing that ever shortens a probe.
            AttrMirrorRebuild(_attrMirror.Length);
        }
        long key = AttrMirrorKey(home, moduleId);
        long[] rows = _attrMirror!;
        int slot = AttrMirrorHash(home, moduleId, _attrMirrorMask);
        int firstFree = -1;
        while (true)
        {
            long k = rows[slot * 2];
            if (k == key) { rows[slot * 2 + 1] = value; return; }
            if (k == AttrMirrorTomb)
            {
                if (firstFree < 0) firstFree = slot;
            }
            else if (k == AttrMirrorEmpty)
            {
                if (firstFree < 0) { firstFree = slot; _attrMirrorUsed++; }
                rows[firstFree * 2] = key;
                rows[firstFree * 2 + 1] = value;
                return;
            }
            slot = (slot + 1) & _attrMirrorMask;
        }
    }

    private void AttrMirrorDelete(int home, int moduleId)
    {
        if (_attrMirror is null) return;
        long key = AttrMirrorKey(home, moduleId);
        long[] rows = _attrMirror;
        int slot = AttrMirrorHash(home, moduleId, _attrMirrorMask);
        while (true)
        {
            long k = rows[slot * 2];
            if (k == AttrMirrorEmpty) return;
            if (k == key)
            {
                rows[slot * 2] = AttrMirrorTomb;
                rows[slot * 2 + 1] = 0;
                return;
            }
            slot = (slot + 1) & _attrMirrorMask;
        }
    }

    /// <summary>Rebuilds the whole image from the store: on growth, on a
    /// tombstone sweep, and after the heap collector moved every index at
    /// once, where re-keying in place would have rows probing past each
    /// other.</summary>
    private void AttrMirrorRebuild(int minCells)
    {
        int live = 0;
        foreach (var unused in AttrAll()) live++;
        int slots = 64;
        while (slots * 2 < minCells) slots *= 2;
        while (slots < (live + 1) * 4) slots *= 2;
        _attrMirror = new long[slots * 2];
        _attrMirrorMask = slots - 1;
        _attrMirrorUsed = 0;
        foreach (var (home, module, value) in AttrAll())
            AttrMirrorPut(home, module, value);
    }

    /// <summary>Asserts the image and the store still say the same thing, in
    /// BOTH directions: every pair the store holds is readable from the image,
    /// and the image holds no pair the store dropped. A one-directional check
    /// would pass over exactly the bug that matters, a row left behind by a
    /// del_attr that the module then reads as live.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    internal void AttrMirrorVerify(string site)
    {
        string? bad = AttrMirrorDisagreement();
        if (bad is not null)
            throw new System.InvalidOperationException($"[ATTR-MIRROR] {site}: {bad}");
    }

    /// <summary>What the two disagree on, or null when they agree. The
    /// JUDGEMENT is unconditional and only the assertion above is switched:
    /// a check compiled out of every build the tests use would pass
    /// vacuously, which is the same as not having one.</summary>
    internal string? AttrMirrorDisagreement()
    {
        if (_attrMirror is null) return null;
        int live = 0;
        foreach (var (home, module, value) in AttrAll())
        {
            live++;
            int got = AttrMirrorLookup(home, module);
            if (got != value)
                return $"var@{home} module={module} store={value} mirror={got}";
        }
        long[] rows = _attrMirror;
        int inImage = 0;
        for (int slot = 0; slot <= _attrMirrorMask; slot++)
        {
            long k = rows[slot * 2];
            if (k == AttrMirrorEmpty || k == AttrMirrorTomb) continue;
            inImage++;
            int home = (int)(k >> 32) - 1;
            int module = (int)k;
            if (module == AttrMirrorCountModule)
            {
                // A count row is a derivation OF the rows, not one of them:
                // checked against the record's size and left out of the
                // tally below.
                inImage--;
                int want = _attrStore.TryGetValue(home, out var r) ? r.Count : 0;
                if ((int)rows[slot * 2 + 1] != want)
                    return $"count row var@{home} image={rows[slot * 2 + 1]} store={want}";
                continue;
            }
            if (AttrValueAt(home, module) != (int)rows[slot * 2 + 1])
                return $"stale row var@{home} module={module}";
        }
        return inImage == live ? null
            : $"{inImage} rows for {live} attributes";
    }

    /// <summary>The image's own answer, reached by the same probe the module
    /// walks. The engine itself reads the store: this is for the cross-check
    /// and its tests.</summary>
    internal int AttrMirrorLookup(int home, int moduleId)
    {
        if (_attrMirror is null) return -1;
        long key = AttrMirrorKey(home, moduleId);
        long[] rows = _attrMirror;
        int slot = AttrMirrorHash(home, moduleId, _attrMirrorMask);
        while (true)
        {
            long k = rows[slot * 2];
            if (k == AttrMirrorEmpty) return -1;
            if (k == key) return (int)rows[slot * 2 + 1];
            slot = (slot + 1) & _attrMirrorMask;
        }
    }
}
