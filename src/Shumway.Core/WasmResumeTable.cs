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

    /// <summary>Atom id -> the fresh-entry marker of the ZERO-ARITY
    /// predicate of that name, 0 when none is covered.
    ///
    /// <para>A second table and not a lookup, because the module cannot do
    /// the lookup: it reads a goal that is a bare ATOM and has its atom id,
    /// but turning (atom, 0) into a functor id means searching the functor
    /// table, and the module can only index. Measured, this is a third of
    /// clpr's remaining deopts -- the control helpers the prelude expands
    /// disjunctions into reach their branches through '$call'/2, and those
    /// branches are atoms.</para></summary>
    public int[] AtomCallMarkers => _atomCallMarkers;

    private int[] _atomCallMarkers = new int[1024];

    /// <summary>Records the fresh-entry marker of a zero-arity predicate by
    /// its atom, alongside the by-functor record.</summary>
    public void SetAtomCallMarker(int atomId, int marker)
    {
        if (atomId < 0) return;
        if (atomId >= _atomCallMarkers.Length)
        {
            int grown = _atomCallMarkers.Length;
            while (grown <= atomId) grown *= 2;
            System.Array.Resize(ref _atomCallMarkers, grown);
        }
        _atomCallMarkers[atomId] = marker;
        CallMarkerVersion++;
    }

    public void ClearAtomCallMarker(int atomId)
    {
        if (atomId >= 0 && atomId < _atomCallMarkers.Length
            && _atomCallMarkers[atomId] > 0)
        {
            _atomCallMarkers[atomId] = 0;
            CallMarkerVersion++;
        }
    }

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
        object? addressMap, int moduleAtomId, int goalKey, int appended,
        int resolvedFid, bool atomGoal = false, int builtinId = -1)
    {
        if (moduleAtomId < 0 || goalKey < 0 || resolvedFid < 0) return;
        // call/N appends arguments, so the predicate it resolves to has a
        // WIDER arity than the goal -- a different functor, which the
        // module cannot derive from the goal's (ids are interned, not
        // computed). It probes with what it has, so that is the key.
        if ((uint)appended > MaxAppended) return;
        // The module id shares the key word with appended and the atom
        // flag; past this it would run into the sign bit. Not cached is
        // the pre-cache behaviour, which is slow and right.
        if (moduleAtomId + 1 >= (1 << 26)) return;
        if (!ReferenceEquals(_metaStamp, addressMap))
        {
            System.Array.Clear(_metaCache, 0, _metaCache.Length);
            _metaCacheUsed = 0;
            _metaStamp = addressMap;
            MetaCacheVersion++;
        }
        if (builtinId >= 0) PublishBuiltin(resolvedFid, builtinId);
        else EnsureCallMarker(resolvedFid);
        long key = MetaKey(moduleAtomId, goalKey, appended, atomGoal);
        int slot = MetaProbe(moduleAtomId, goalKey, appended, atomGoal,
                             _metaCacheMask);
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

    /// <summary>Makes sure the module has SOMETHING to jump to for this
    /// functor.
    ///
    /// <para>A meta-call and an ordinary call end on the same instruction:
    /// stage a marker in <see cref="WasmAbi.Pc"/> and return
    /// SuccessTailCall. They differ only in where the marker comes from.
    /// An ordinary call knows its callee when it is compiled and BAKES
    /// marker(callee, 0); a meta-call learns it at run time and has to read
    /// it out of this table.</para>
    ///
    /// <para>The table was filled only when a module was installed, so a
    /// meta-call landing on a predicate nothing compiled -- one the census
    /// refused, one below the threshold -- read zero and stepped aside,
    /// while a direct call to that same predicate did not. There is no
    /// reason for the asymmetry: marker(fid, 0) with no row of its own is
    /// resolved by the host to the predicate's live bytecode entry, which
    /// is exactly what Tier 0 would have done.</para>
    ///
    /// <para>Self-healing rather than eager: the first meta-call to a
    /// functor still goes to the host, which is where this runs, and every
    /// one after it has a target. Interning a marker for every functor up
    /// front would grow the pool by predicates no meta-call ever names.
    /// </para></summary>
    private void EnsureCallMarker(int functorId)
    {
        if (functorId < 0) return;
        if (functorId < _callMarkers.Length && _callMarkers[functorId] > 0)
            return;
        SetCallMarker(functorId, Activation.EncodeResumeMarker(functorId, 0));
    }

    /// <summary>A functor the host resolves to a DIRECT builtin gets a
    /// NEGATIVE marker, -(builtin id + 1): the module reads it where it
    /// reads a predicate's marker and, instead of jumping, requests the
    /// builtin with the goal's arguments in the registers -- the same exit
    /// a call_builtin site makes. Zero stays "nobody covers this".
    /// A zero-arity builtin is an ATOM goal, keyed by its atom too.</summary>
    public void PublishBuiltin(int functorId, int builtinId)
    {
        if (functorId < 0 || builtinId < 0) return;
        int marker = -(builtinId + 1);
        if (functorId >= _callMarkers.Length || _callMarkers[functorId] <= 0)
            SetCallMarker(functorId, marker);
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        if (arity == 0 && (atomId >= _atomCallMarkers.Length || _atomCallMarkers[atomId] <= 0))
            SetAtomCallMarker(atomId, marker);
    }

    /// <summary>The probe's first slot. The module recomputes this exact
    /// function, so the two move together or not at all.</summary>
    public static int MetaProbe(int moduleAtomId, int goalKey, int appended,
                                bool atomGoal, int mask)
    {
        uint h = (uint)moduleAtomId * 2654435761u + (uint)goalKey * 2246822519u
              + (uint)appended * 2166136261u
              + (atomGoal ? 1u : 0u) * 2654435789u;
        h ^= h >> 15;
        return (int)(h & (uint)mask);
    }

    /// <summary>The key a slot holds. The module packs the same word, so
    /// the two move together or not at all.
    ///
    /// <para>For a COMPOUND goal the key half is its functor id; for an
    /// ATOM goal it is the atom id, and the two id spaces overlap, hence
    /// the flag. An atom goal cannot be keyed by functor at all: the
    /// callee is name/appended and a module cannot intern that id.</para>
    /// </summary>
    public static long MetaKey(int moduleAtomId, int goalKey, int appended,
                               bool atomGoal = false)
        => ((long)(moduleAtomId + 1) << 36)
         | ((atomGoal ? 1L : 0L) << 35)
         | ((long)(appended & MaxAppended) << 32)
         | (uint)goalKey;

    /// <summary>The widest call/N the key can carry, and the reason it is
    /// three bits: call/8 appends seven.</summary>
    public const int MaxAppended = 7;

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

    public int MetaLookup(int moduleAtomId, int goalKey, int appended = 0,
                          bool atomGoal = false)
    {
        long key = MetaKey(moduleAtomId, goalKey, appended, atomGoal);
        int slot = MetaProbe(moduleAtomId, goalKey, appended, atomGoal,
                             _metaCacheMask);
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
        if (functorId >= 0 && functorId < _callMarkers.Length
            && _callMarkers[functorId] > 0)
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
