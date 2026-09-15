namespace Shumway.Core;

/// <summary>The rows a wasm module reads to resolve a resume marker: one i64
/// per marker, indexed by <c>marker - Activation.ResumeMarkerBase</c>.
///
/// <para>The index needs no computing. <see cref="Activation.EncodeResumeMarker"/>
/// interns each (functor, address) pair and returns <c>Base + denseId</c>, so a
/// marker IS a dense id and resolving one is a subscript. What that replaces is
/// a chain of baked comparisons — <c>if (bp == c1) ... if (bp == c2) ...</c>,
/// one per choice-point site — walked linearly on every failure.</para>
///
/// <para>It also collapses two encodings of one fact into one. The host
/// resolves a marker through a dictionary and the module through its baked
/// chain; those are derived separately today, and a disagreement between them
/// is a bug with nowhere to show. Reading the same rows makes that class of bug
/// impossible rather than unlikely.</para>
///
/// <para>An instance belongs to ONE ENGINE, and every module of that engine
/// shares it. That is the whole point: a module resolving a marker has to be
/// able to discover that it belongs to a DIFFERENT module and where that one
/// is, which it cannot do from a table only it can see.</para>
///
/// <para>Engine, though, and never process-wide. Functor ids and the marker
/// pool are global, but bytecode addresses belong to an engine's code space,
/// so two engines running the same program mint the same markers for different
/// code. Sharing across engines would resolve one engine's marker into
/// another's module — and the desktop tests, which build dozens of engines in
/// a process, would be the first to find out.</para></summary>
public sealed class WasmResumeTable
{
    // Row: ((moduleId + 1) << 32) | cursor. Zero is "not here", which has to
    // be distinguishable from a valid cursor 0 (the first leader of a module).
    private long[] _rows;

    public WasmResumeTable(int initialRows = 4096)
        => _rows = new long[initialRows];

    /// <summary>Hands out the next module id for this engine. Ids are dense
    /// and start at 0, so they index the moduleId -&gt; table-index array the
    /// in-wasm hop reads.</summary>
    public int NextModuleId() => _nextModuleId++;

    private int _nextModuleId;

    /// <summary>How many modules this engine has handed ids to.</summary>
    public int ModuleCount => _nextModuleId;

    /// <summary>The rows, for the host to copy into linear memory. Grown by
    /// <see cref="Set"/>; the reference changes, so read it each time.</summary>
    public long[] Rows => _rows;

    public int Length => _rows.Length;

    /// <summary>Functor id -> the RESUME MARKER of that functor's fresh entry,
    /// 0 for a functor no module here covers.
    ///
    /// <para>This is what lets a module call a goal it only learns at RUN
    /// time. A marker is interned by the host from a (functor, address) pair
    /// and handed back as Base + a dense id, so it cannot be computed from a
    /// functor -- which is why every call the emitter bakes needs a callee
    /// known at compile time, and why a meta-call could only step aside. With
    /// this table the module reads the marker for a runtime functor and then
    /// takes the ordinary resume probe, unchanged.</para>
    ///
    /// <para>Measured, that is the whole cost of clpfd's propagation loop:
    /// `clpfd_run([P|Ps]) :- call(P), clpfd_run(Ps)` steps aside once per
    /// propagator, 16,378 times in one queens 12 run -- 86% of every deopt
    /// there is.</para></summary>
    public int[] CallMarkers => _callMarkers;

    /// <summary>Bumped by every change to <see cref="CallMarkers"/>. The
    /// table changes only when a module is installed or evicted -- rare --
    /// so a world that has to COPY it into its own memory can skip the copy
    /// whenever this has not moved. The attribute image gets no such stamp
    /// and cannot: it changes on every put_attr.</summary>
    public int CallMarkerVersion { get; private set; }

    private int[] _callMarkers = new int[1024];

    /// <summary>Records the fresh-entry marker of a functor. Grows to fit:
    /// functor ids are dense and global, so the table is indexed directly
    /// rather than hashed.</summary>
    public void SetCallMarker(int functorId, int marker)
    {
        if (functorId < 0) return;
        if (functorId >= _callMarkers.Length)
        {
            int grown = _callMarkers.Length;
            while (grown <= functorId) grown *= 2;
            System.Array.Resize(ref _callMarkers, grown);
        }
        _callMarkers[functorId] = marker;
        CallMarkerVersion++;
    }

    /// <summary>The meta-call inline cache: (module atom, goal functor) ->
    /// the RESOLVED functor. Two i64 per slot, key
    /// <c>((module + 1) &lt;&lt; 32) | goalFid</c> then the resolved id;
    /// key 0 is an empty slot. Open-addressed on the same probe the
    /// attribute image uses, because a module walks it the same way.
    ///
    /// <para>It holds the resolved FUNCTOR, not the marker, and that is the
    /// point: the module reads the marker out of
    /// <see cref="CallMarkers"/> afterwards, so an eviction -- which already
    /// zeroes that row -- invalidates this cache without touching it. A cache
    /// that stored the marker would need its own invalidation, and the one
    /// thing worse than a slow meta-call is one that jumps into a module that
    /// no longer owns the predicate.</para>
    ///
    /// <para>Filled by the HOST when it resolves, which it was doing anyway:
    /// the module cannot resolve a module-tagged goal, since that is a lookup
    /// through a module's locals and imports.</para></summary>
    public long[] MetaCache => _metaCache;

    public int MetaCacheMask => _metaCacheMask;

    /// <summary>Entries the host has filled. Zero after a run that meta-calls
    /// says the observer never fired -- a different fault from a module that
    /// reads the cache and declines.</summary>
    public int MetaCacheCount => _metaCacheUsed;

    /// <summary>Bumped by every fill, so a copying world knows when to
    /// re-stage. Fills stop once the working set is in, so this settles.
    /// </summary>
    public int MetaCacheVersion { get; private set; }

    private long[] _metaCache = new long[512 * 2];
    private int _metaCacheMask = 511;
    private int _metaCacheUsed;

    /// <summary>The address map this cache was built against. A resolution
    /// is only valid for the map that produced it: a new query links a new
    /// one, and a predicate the old map sent somewhere may now live
    /// elsewhere. The engine's own meta-route cache is stamped the same way
    /// and discarded on the same event -- see MetaRoute.cs, whose soundness
    /// argument this cache lives or dies by.
    ///
    /// <para>Without it the cache outlives what it derives from, because it
    /// hangs off the world rather than off the query. The failure is silent
    /// and it is the worst kind: the module jumps to a predicate that is
    /// still covered and still takes that many arguments, and answers.</para>
    /// </summary>
    private object? _metaStamp;

    /// <summary>Records what the host resolved, against the address map that
    /// resolved it. Full is not an error: the cache stops taking entries and
    /// those goals keep going to the host, which is what they did before
    /// there was a cache.</summary>
    public void NoteMetaResolution(
        object? addressMap, int moduleAtomId, int goalFid, int resolvedFid)
    {
        if (moduleAtomId < 0 || goalFid < 0 || resolvedFid < 0) return;
        if (!ReferenceEquals(_metaStamp, addressMap))
        {
            System.Array.Clear(_metaCache, 0, _metaCache.Length);
            _metaCacheUsed = 0;
            _metaStamp = addressMap;
            MetaCacheVersion++;
        }
        long key = ((long)(moduleAtomId + 1) << 32) | (uint)goalFid;
        int slot = MetaProbe(moduleAtomId, goalFid, _metaCacheMask);
        for (int probe = 0; probe <= _metaCacheMask; probe++)
        {
            long k = _metaCache[slot * 2];
            if (k == key)
            {
                if (_metaCache[slot * 2 + 1] != resolvedFid)
                {
                    _metaCache[slot * 2 + 1] = resolvedFid;
                    MetaCacheVersion++;
                }
                return;
            }
            if (k == 0)
            {
                if (_metaCacheUsed * 4 >= _metaCache.Length) return;   // full
                _metaCache[slot * 2] = key;
                _metaCache[slot * 2 + 1] = resolvedFid;
                _metaCacheUsed++;
                MetaCacheVersion++;
                return;
            }
            slot = (slot + 1) & _metaCacheMask;
        }
    }

    /// <summary>The probe's first slot. The module recomputes this exact
    /// function, so the two move together or not at all.</summary>
    public static int MetaProbe(int moduleAtomId, int goalFid, int mask)
    {
        uint h = (uint)moduleAtomId * 2654435761u + (uint)goalFid * 2246822519u;
        h ^= h >> 15;
        return (int)(h & (uint)mask);
    }

    /// <summary>What the cache says, or -1. For the cross-check and its
    /// tests; the engine itself resolves.</summary>
    /// <summary>Drops every entry. The world calls this when it can no
    /// longer vouch for the map they were resolved against.</summary>
    public void ClearMetaCache()
    {
        if (_metaCacheUsed == 0) return;
        System.Array.Clear(_metaCache, 0, _metaCache.Length);
        _metaCacheUsed = 0;
        _metaStamp = null;
        MetaCacheVersion++;
    }

    public int MetaLookup(int moduleAtomId, int goalFid)
    {
        long key = ((long)(moduleAtomId + 1) << 32) | (uint)goalFid;
        int slot = MetaProbe(moduleAtomId, goalFid, _metaCacheMask);
        for (int probe = 0; probe <= _metaCacheMask; probe++)
        {
            long k = _metaCache[slot * 2];
            if (k == 0) return -1;
            if (k == key) return (int)_metaCache[slot * 2 + 1];
            slot = (slot + 1) & _metaCacheMask;
        }
        return -1;
    }

    /// <summary>Forgets a functor's fresh-entry marker. The marker itself
    /// stays valid -- it is interned for the life of the process -- but its
    /// ROW is cleared alongside, so a module that reads the marker anyway
    /// merely fails the resume probe and exits to the host. Clearing here is
    /// belt and braces, and cheap.</summary>
    public void ClearCallMarker(int functorId)
    {
        if (functorId >= 0 && functorId < _callMarkers.Length)
        {
            _callMarkers[functorId] = 0;
            CallMarkerVersion++;
        }
    }

    /// <summary>Records where a marker resolves. <paramref name="cursor"/> is
    /// the owning module's own dispatch cursor.</summary>
    public void Set(int marker, int moduleId, int cursor)
    {
        int i = marker - Activation.ResumeMarkerBase;
        if (i < 0) throw new ArgumentOutOfRangeException(nameof(marker),
            $"0x{marker:X} is not a resume marker.");
        if (i >= _rows.Length)
        {
            int grown = _rows.Length;
            while (grown <= i) grown *= 2;
            Array.Resize(ref _rows, grown);
        }
        _rows[i] = ((long)(moduleId + 1) << 32) | (uint)cursor;
    }

    /// <summary>Forgets every row of one module: what an eviction does. Linear
    /// in the table, and eviction is rare — the alternative is a per-module
    /// index that would have to be kept correct for a case that almost never
    /// runs.</summary>
    public void ClearModule(int moduleId)
    {
        long tag = (long)(moduleId + 1) << 32;
        for (int i = 0; i < _rows.Length; i++)
            if ((_rows[i] & ~0xFFFFFFFFL) == tag) _rows[i] = 0;
    }

    /// <summary>Forgets every row of one functor: what an eviction, or a
    /// takeover by a newer module, does. Linear in the table; both are
    /// rare.</summary>
    public void ClearFunctor(int functorId)
    {
        for (int i = 0; i < _rows.Length; i++)
        {
            if (_rows[i] == 0) continue;
            if (Activation.DecodeResumeMarker(Activation.ResumeMarkerBase + i).FunctorId
                == functorId)
                _rows[i] = 0;
        }
    }

    /// <summary>Reads a row back. False when the marker does not resolve here,
    /// which is what the host and the module both treat as "not mine".</summary>
    public bool TryGet(int marker, out int moduleId, out int cursor)
    {
        moduleId = 0;
        cursor = 0;
        int i = marker - Activation.ResumeMarkerBase;
        if ((uint)i >= (uint)_rows.Length) return false;
        long row = _rows[i];
        if (row == 0) return false;
        moduleId = (int)(row >> 32) - 1;
        cursor = (int)row;
        return true;
    }
}
