namespace Shumway.Core;

/// <summary>
/// Global atom table with the three-tier storage strategy from ADR-003.
///
/// <list type="bullet">
///   <item><b>Permanent</b> — strong refs from <see cref="_permanentById"/>; never collected.</item>
///   <item><b>Transient</b> — strong refs from <see cref="_transientById"/>; can be moved to
///         TransientWeak or removed by <see cref="Sweep"/>.</item>
///   <item><b>TransientWeak</b> — only a <see cref="WeakReference{T}"/>; the table itself
///         does not keep the atom alive. It survives only as long as foreign C# code retains
///         a strong reference.</item>
/// </list>
///
/// The by-name index <see cref="_byName"/> holds <see cref="WeakReference{T}"/> entries so
/// that demoting an atom to TransientWeak truly releases all table-side strong references —
/// otherwise the weak-ref tier would be pointless. Atom identity is preserved across tiers
/// because all lookups (Intern by name, GetById) hit the same Atom instance as long as it
/// is alive.
///
/// All mutating operations and <see cref="GetById"/> hold <see cref="_lock"/>. The GC's
/// mark phase is performed externally by the engine subsystem; this table implements the
/// sweep (phases 2-4 of ADR-003) given the set of reachable atom ids as input.
/// </summary>
public static class AtomTable
{
    // Pre-registered atom ids. Ids 0..4 are populated; 5..15 are reserved for future
    // pre-registered atoms; user atoms start at FirstUserId.
    public const int EmptyListId = 0;
    public const int EmptyBracesId = 1;
    public const int ConsFunctorId = 2;
    public const int TrueId = 3;
    public const int FalseId = 4;
    public const int FirstUserId = 16;

    // Chunk 222: ConcurrentDictionary lets Intern probe transients
    // lock-free on the fast path, mirroring the chunk-214 lock-free
    // path for permanents. Single-threaded engines saw
    // Monitor.Enter_Slowpath as the 6th-hottest function in dotnet-
    // trace because every transient atom creation (atom_chars per-
    // character loops, get_char streams, atom_concat results, etc.)
    // took AtomTable._lock. Concurrent reads here are wait-free;
    // writes still go through _lock so the multi-step bookkeeping
    // (id allocation, by-id dictionaries, permanent mirror) stays
    // consistent.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WeakReference<Atom>> _byName = new();
    private static readonly Dictionary<int, Atom> _permanentById = new();
    private static readonly Dictionary<int, Atom> _transientById = new();
    private static readonly Dictionary<int, TransientWeakEntry> _transientWeak = new();
    private static readonly List<WeakReference<Atom>> _foreignWeakRefs = new();
    // Chunk 232 — System.Threading.Lock (new in .NET 9) is markedly
    // faster than the legacy object-based monitor on uncontended paths.
    // For Blint, AtomTable.Intern shows up at ~1.8% inclusive in
    // dotnet-trace with the chunk-222 lock-free fast paths in place;
    // the ~1.2% in children is mostly Monitor.Enter_Slowpath when
    // the transient-promotion or new-atom slow path acquires _lock.
    // System.Threading.Lock skips the syncblock dance and uses a
    // dedicated state machine with a true fast path that the JIT
    // can inline.
    private static readonly System.Threading.Lock _lock = new();
    private static int _nextId = FirstUserId;

    // Chunk 167: fast-path for the most common GetById case. Permanent
    // atoms (string literals, public predicate names, the single-char
    // cache, etc.) get dense ids and far outnumber lookups on transient
    // / weak ids. A flat array indexed by id, grown copy-on-write, lets
    // the dispatcher / TermReader skip the lock + dictionary probe on
    // the common case. Misses (uncached id, transient, or weak) fall
    // through to the locked path.
    private static volatile Atom?[] _permanentByIdArray = System.Array.Empty<Atom?>();

    // Lock-free by-NAME fast path for permanent atoms — the Intern
    // counterpart of _permanentByIdArray (which serves GetById). A
    // permanent atom's identity is stable and it is never collected, so a
    // hit here needs no lock. Profiling Blint showed AtomTable.Intern's
    // global lock at ~16% of wall time, contended against the background
    // Tier-1 promotion thread; the dominant callers (atom_chars over the
    // pre-interned single-char ASCII atoms, predicate-name interning in
    // assert/retract) re-intern atoms that are already permanent, so this
    // dictionary takes them off the lock entirely. Written only under
    // _lock at every permanent-creation / promotion site; read lock-free.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Atom> _permanentByName = new();

    private readonly struct TransientWeakEntry
    {
        public readonly WeakReference<Atom> Weak;
        public readonly string Name;
        public TransientWeakEntry(WeakReference<Atom> weak, string name) { Weak = weak; Name = name; }
    }

    // Chunk 166: cache of single-character atom ids. Chunk 222 widened
    // it from 128 (ASCII) to 256 (Latin-1 — what `char (byte)` covers
    // and what every other major Prolog (GNU, SWI, SICStus) pre-
    // interns). The character-I/O builtins (peek_char/get_char/put_char),
    // `char_code/2`, and the per-character loops in atom_chars / string_chars
    // all hit this cache, so any code point in 0..255 skips the lock +
    // dictionary probe + 1-char string allocation that Intern would do.
    public const int SingleCharAtomCacheLimit = 256;
    private static readonly int[] _singleCharAtomIds = new int[SingleCharAtomCacheLimit];

    /// <summary>Number of permanent atoms a freshly-reset table holds:
    /// the five pre-registered specials (<c>[]</c>, <c>{}</c>, <c>.</c>,
    /// <c>true</c>, <c>false</c>) plus the chunk-166 single-char ASCII
    /// cache (codes 0..127). <c>.</c> is in both sets so it counts
    /// once. Tests use this as the baseline when asserting on
    /// <see cref="PermanentCount"/> after an intern.</summary>
    public const int PreRegisteredPermanentCount = 5 + SingleCharAtomCacheLimit - 1;

    static AtomTable()
    {
        AddPreRegisteredLocked(EmptyListId, "[]");
        AddPreRegisteredLocked(EmptyBracesId, "{}");
        AddPreRegisteredLocked(ConsFunctorId, ".");
        AddPreRegisteredLocked(TrueId, "true");
        AddPreRegisteredLocked(FalseId, "false");

        // Pre-intern single-character atoms 0..127 as permanent atoms
        // and remember their ids in a flat int array for lock-free
        // lookup. "[]" / "." are already permanent from above; their
        // codes get patched in below so the cache stays consistent.
        RebuildSingleCharCacheLocked();
    }

    /// <summary>Returns the pre-interned permanent atom id for the
    /// single-character atom whose only code is <paramref name="code"/>,
    /// or <c>-1</c> if <paramref name="code"/> is outside the cached
    /// range. The cache is populated at class-load time so this is a
    /// pure array index — no lock, no allocation. Used by character-
    /// I/O builtins (chunk 166).</summary>
    public static int GetSingleCharAtomId(int code)
    {
        if ((uint)code >= (uint)SingleCharAtomCacheLimit) return -1;
        return _singleCharAtomIds[code];
    }

    private static void AddPreRegisteredLocked(int id, string name)
    {
        var atom = new Atom(id, name, isPermanent: true);
        _byName[name] = new WeakReference<Atom>(atom);
        _permanentById[id] = atom;
        StorePermanentInArrayLocked(id, atom);
    }

    private static void StorePermanentInArrayLocked(int id, Atom atom)
    {
        Atom?[] arr = _permanentByIdArray;
        if (id >= arr.Length)
        {
            int newSize = arr.Length == 0 ? 256 : arr.Length * 2;
            while (newSize <= id) newSize *= 2;
            var newArr = new Atom?[newSize];
            System.Array.Copy(arr, newArr, arr.Length);
            arr = newArr;
        }
        arr[id] = atom;
        _permanentByIdArray = arr;
        // Mirror into the lock-free by-name fast path (every created-
        // permanent atom flows through here).
        _permanentByName[atom.Name] = atom;
    }

    /// <summary>
    /// Returns the canonical <see cref="Atom"/> for <paramref name="name"/>, allocating one
    /// on first use. Passing <c>permanent: true</c> promotes an existing transient atom.
    /// </summary>
    public static Atom Intern(string name, bool permanent = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        // Lock-free fast path: a permanent atom satisfies any intern
        // request (permanent or not) because its identity is stable and it
        // is never collected. Covers the hot tokenizer path and most
        // predicate-name interning without touching _lock.
        if (_permanentByName.TryGetValue(name, out var perm))
            return perm;
        // Chunk 222: lock-free fast path for transient atoms. The
        // existing _byName WeakReference is published under _lock but
        // read here without — ConcurrentDictionary makes that safe.
        // The WeakReference target may have been GC-collected since
        // publication; treat that as a miss and fall through. If the
        // caller asked for permanent: true and the atom we found is
        // still transient, fall through too so PromoteToPermanentLocked
        // runs under the lock (rare path — almost every transient
        // re-intern is a permanent:false request).
        if (!permanent
            && _byName.TryGetValue(name, out var liveRef)
            && liveRef.TryGetTarget(out var alive))
        {
            return alive;
        }
        lock (_lock)
        {
            if (_byName.TryGetValue(name, out var weakRef) && weakRef.TryGetTarget(out var existing))
            {
                if (permanent && !existing.IsPermanent)
                    PromoteToPermanentLocked(existing);
                return existing;
            }

            int id = _nextId++;
            var atom = new Atom(id, name, isPermanent: permanent);
            _byName[name] = new WeakReference<Atom>(atom);
            if (permanent)
            {
                _permanentById[id] = atom;
                StorePermanentInArrayLocked(id, atom);
            }
            else
                _transientById[id] = atom;
            return atom;
        }
    }

    /// <summary>Returns the atom with <paramref name="id"/>, or <c>null</c> if no live atom carries that id.</summary>
    public static Atom? GetById(int id)
    {
        // Chunk 167: lock-free fast-path for permanent atoms via a
        // dense array. The dispatcher's hot path (every atom name
        // resolution during term materialisation, every atom_concat /
        // char_code result, etc.) lands here, and permanents
        // dominate the working set — they're keyed off id directly
        // with no lock + dictionary probe needed.
        Atom?[] permArr = _permanentByIdArray;
        if ((uint)id < (uint)permArr.Length)
        {
            var p = permArr[id];
            if (p is not null) return p;
        }
        lock (_lock)
        {
            if (_permanentById.TryGetValue(id, out var atom))
                return atom;
            if (_transientById.TryGetValue(id, out atom))
                return atom;
            if (_transientWeak.TryGetValue(id, out var entry) && entry.Weak.TryGetTarget(out atom))
                return atom;
            return null;
        }
    }

    /// <summary>
    /// Records that <paramref name="atom"/> has been exposed to C# code through the embedding
    /// API. While C# retains the reference, the atom will not be collected even if no engine
    /// references it. No-op for permanent atoms.
    /// </summary>
    public static void RegisterForeignHold(Atom atom)
    {
        ArgumentNullException.ThrowIfNull(atom);
        if (atom.IsPermanent)
            return;
        lock (_lock)
        {
            _foreignWeakRefs.Add(new WeakReference<Atom>(atom));
        }
    }

    /// <summary>
    /// Implements phases 2-4 of the atom GC from ADR-003. <paramref name="reachable"/> is the
    /// set of atom ids reachable from engine state (heaps, stacks, registers, predicate
    /// metadata); the caller is responsible for computing it (mark phase).
    /// </summary>
    public static void Sweep(HashSet<int> reachable)
    {
        ArgumentNullException.ThrowIfNull(reachable);
        lock (_lock)
        {
            // Phase 2: compact foreign-hold weak refs and collect ids still alive in C#.
            var foreignAlive = new HashSet<int>();
            for (int i = _foreignWeakRefs.Count - 1; i >= 0; i--)
            {
                if (_foreignWeakRefs[i].TryGetTarget(out var atom))
                    foreignAlive.Add(atom.Id);
                else
                    _foreignWeakRefs.RemoveAt(i);
            }

            // Phase 3: walk Transient. Keep if reachable; demote to TransientWeak if only C#
            // holds it; otherwise drop entirely.
            var toDemote = new List<KeyValuePair<int, Atom>>();
            var toDrop = new List<KeyValuePair<int, Atom>>();
            foreach (var kv in _transientById)
            {
                if (reachable.Contains(kv.Key))
                    continue;
                if (foreignAlive.Contains(kv.Key))
                    toDemote.Add(kv);
                else
                    toDrop.Add(kv);
            }
            foreach (var kv in toDemote)
            {
                _transientById.Remove(kv.Key);
                _transientWeak[kv.Key] = new TransientWeakEntry(new WeakReference<Atom>(kv.Value), kv.Value.Name);
                // _byName: keep — its weak entry still references the live atom (foreign C# holds it).
            }
            foreach (var kv in toDrop)
            {
                _transientById.Remove(kv.Key);
                _byName.TryRemove(kv.Value.Name, out _);
            }

            // Phase 4: walk TransientWeak. Promote back to Transient if an engine resurrected
            // it; drop entirely if the .NET GC has collected the atom; otherwise leave alone.
            var weakToPromote = new List<KeyValuePair<int, Atom>>();
            var weakToRemove = new List<KeyValuePair<int, string>>();
            foreach (var kv in _transientWeak)
            {
                if (!kv.Value.Weak.TryGetTarget(out var atom))
                    weakToRemove.Add(new KeyValuePair<int, string>(kv.Key, kv.Value.Name));
                else if (reachable.Contains(kv.Key))
                    weakToPromote.Add(new KeyValuePair<int, Atom>(kv.Key, atom));
            }
            foreach (var kv in weakToPromote)
            {
                _transientWeak.Remove(kv.Key);
                _transientById[kv.Key] = kv.Value;
            }
            foreach (var kv in weakToRemove)
            {
                _transientWeak.Remove(kv.Key);
                _byName.TryRemove(kv.Value, out _);
            }
        }
    }

    private static void PromoteToPermanentLocked(Atom atom)
    {
        if (atom.IsPermanent)
            return;
        atom.IsPermanent = true;
        _permanentById[atom.Id] = atom;
        _transientById.Remove(atom.Id);
        _transientWeak.Remove(atom.Id);
        // A promoted atom is now eligible for the lock-free by-name path.
        _permanentByName[atom.Name] = atom;
    }

    // ---------- Diagnostics / test helpers ----------

    internal static int PermanentCount { get { lock (_lock) return _permanentById.Count; } }
    internal static int TransientCount { get { lock (_lock) return _transientById.Count; } }
    internal static int TransientWeakCount { get { lock (_lock) return _transientWeak.Count; } }
    internal static int ForeignHoldCount { get { lock (_lock) return _foreignWeakRefs.Count; } }

    /// <summary>
    /// Wipes all state back to the pre-registered defaults. Intended only for test isolation.
    /// </summary>
    internal static void ResetForTesting()
    {
        lock (_lock)
        {
            _byName.Clear();
            _permanentById.Clear();
            _transientById.Clear();
            _transientWeak.Clear();
            _foreignWeakRefs.Clear();
            // Chunk 167: clear the array fast-path too. Otherwise a
            // post-reset Intern that lands on a stale slot (because
            // _nextId restarts at FirstUserId but the array still
            // holds prior atoms at those ids) would return the
            // stale atom from GetById's fast path.
            _permanentByIdArray = System.Array.Empty<Atom?>();
            // Clear the by-name fast path too; the AddPreRegistered /
            // RebuildSingleCharCache calls below re-populate it.
            _permanentByName.Clear();
            _nextId = FirstUserId;
            AddPreRegisteredLocked(EmptyListId, "[]");
            AddPreRegisteredLocked(EmptyBracesId, "{}");
            AddPreRegisteredLocked(ConsFunctorId, ".");
            AddPreRegisteredLocked(TrueId, "true");
            AddPreRegisteredLocked(FalseId, "false");
            // Chunk 166: rebuild the single-char-atom cache too —
            // ResetForTesting just wiped the underlying atoms, so the
            // pre-populated ids in _singleCharAtomIds are stale.
            RebuildSingleCharCacheLocked();
        }
    }

    private static void RebuildSingleCharCacheLocked()
    {
        for (int i = 0; i < SingleCharAtomCacheLimit; i++)
        {
            string s = ((char)i).ToString();
            if (_byName.TryGetValue(s, out var existingWeak)
                && existingWeak.TryGetTarget(out var existing))
            {
                _singleCharAtomIds[i] = existing.Id;
                if (!existing.IsPermanent) PromoteToPermanentLocked(existing);
                continue;
            }
            int id = _nextId++;
            var atom = new Atom(id, s, isPermanent: true);
            _byName[s] = new WeakReference<Atom>(atom);
            _permanentById[id] = atom;
            StorePermanentInArrayLocked(id, atom);
            _singleCharAtomIds[i] = id;
        }
    }
}
