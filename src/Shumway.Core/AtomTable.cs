namespace Shumway.Core;

/// <summary>
/// Global atom table with the three tiers of ADR-003.
///
/// <list type="bullet">
///   <item><b>Permanent</b>: strong refs from <see cref="_permanentById"/>; never collected.</item>
///   <item><b>Transient</b>: strong refs from <see cref="_transientById"/>, alive while a
///         claimant holds it or since its last intern not every claimant has marked
///         (<see cref="Collect"/>).</item>
///   <item><b>TransientWeak</b>: only a <see cref="WeakReference{T}"/>; it survives as long as
///         foreign C# code retains the <see cref="Atom"/>.</item>
/// </list>
///
/// The by-name index <see cref="_byName"/> holds <see cref="WeakReference{T}"/> entries so
/// that a TransientWeak atom has no table-side strong reference. A dead atom's id is
/// handed out again: a holder the marks miss reads another atom, which
/// <see cref="PoisonDeadIds"/> turns into an exception for tests.
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

    // Reads are lock-free; writes go through _lock so the multi-step bookkeeping
    // (id allocation, by-id dictionaries, the id array) stays consistent.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WeakReference<Atom>> _byName = new();
    private static readonly Dictionary<int, Atom> _permanentById = new();
    private static readonly Dictionary<int, Atom> _transientById = new();
    private static readonly Dictionary<int, TransientWeakEntry> _transientWeak = new();
    private static readonly List<WeakReference<Atom>> _foreignWeakRefs = new();
#if NETFRAMEWORK
    private static readonly object _lock = new();
#else
    private static readonly System.Threading.Lock _lock = new();
#endif
    private static int _nextId = FirstUserId;

    // The ids of dead atoms, handed out again last in first out: an id a holder
    // failed to claim comes back soon, where the tests that run with reuse meet it.
    private static int[] _freeIds = System.Array.Empty<int>();
    private static int _freeCount;

    // Under PoisonDeadIds, the dead ids with their names, kept out of the free list.
    private static readonly Dictionary<int, string> _poisoned = new();
    private static bool _poisonDeadIds;

    // Every strongly held atom by id, permanent and transient, grown copy-on-write:
    // GetById and the claims read it without the lock. A TransientWeak or dead atom
    // has no slot (null), the first so the table keeps no strong reference to it.
    private static volatile Atom?[] _byIdArray = System.Array.Empty<Atom?>();

    // Lock-free by-name path for permanent atoms: a permanent intern request is
    // satisfied by a hit here with no lock. Written only under _lock.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Atom> _permanentByName = new();

    // The global epoch. A claimant's mark takes a new value (BeginMark); an intern
    // and a lost claim stamp an atom with the current one.
    private static long _epoch;

    // The live claimants, under _lock: a collection takes the oldest last mark among
    // them, and one registering meanwhile is seen or registers after the minimum.
    private static readonly List<AtomClaimant> _claimants = new();

    // The epoch a requested collection waits for every claimant to have marked
    // at; 0 with none requested. Written under _lock.
    private static long _pendingTarget;

    private readonly struct TransientWeakEntry
    {
        public readonly WeakReference<Atom> Weak;
        public readonly string Name;
        public readonly WeakReference<Atom> NameRef;
        public TransientWeakEntry(WeakReference<Atom> weak, string name, WeakReference<Atom> nameRef)
        { Weak = weak; Name = name; NameRef = nameRef; }
    }

    // Single-character atoms 0..255 are pre-interned permanent; their ids are a flat
    // array lookup for the character builtins.
    public const int SingleCharAtomCacheLimit = 256;
    private static readonly int[] _singleCharAtomIds = new int[SingleCharAtomCacheLimit];

    /// <summary>Number of permanent atoms a freshly-reset table holds:
    /// the five pre-registered specials (<c>[]</c>, <c>{}</c>, <c>.</c>,
    /// <c>true</c>, <c>false</c>) plus the single-character cache.
    /// <c>.</c> is in both sets so it counts once.</summary>
    public const int PreRegisteredPermanentCount = 5 + SingleCharAtomCacheLimit - 1;

    static AtomTable()
    {
        AddPreRegisteredLocked(EmptyListId, "[]");
        AddPreRegisteredLocked(EmptyBracesId, "{}");
        AddPreRegisteredLocked(ConsFunctorId, ".");
        AddPreRegisteredLocked(TrueId, "true");
        AddPreRegisteredLocked(FalseId, "false");
        RebuildSingleCharCacheLocked();
    }

    /// <summary>Returns the pre-interned permanent atom id for the
    /// single-character atom whose only code is <paramref name="code"/>,
    /// or <c>-1</c> if <paramref name="code"/> is outside the cached range.</summary>
    public static int GetSingleCharAtomId(int code)
    {
        if ((uint)code >= (uint)SingleCharAtomCacheLimit) return -1;
        return _singleCharAtomIds[code];
    }

    private static void AddPreRegisteredLocked(int id, string name)
    {
        var atom = new Atom(id, name, isPermanent: true);
        atom.NameRef = new WeakReference<Atom>(atom);
        _byName[name] = atom.NameRef;
        _permanentById[id] = atom;
        _permanentByName[name] = atom;
        StoreInArrayLocked(id, atom);
    }

    private static void StoreInArrayLocked(int id, Atom? atom)
    {
        Atom?[] arr = _byIdArray;
        if (id >= arr.Length)
        {
            if (atom is null) return;
            int newSize = arr.Length == 0 ? 256 : arr.Length * 2;
            while (newSize <= id) newSize *= 2;
            var newArr = new Atom?[newSize];
            System.Array.Copy(arr, newArr, arr.Length);
            arr = newArr;
        }
        arr[id] = atom;
        _byIdArray = arr;
    }

    /// <summary>
    /// Returns the canonical <see cref="Atom"/> for <paramref name="name"/>, allocating one
    /// on first use. Passing <c>permanent: true</c> promotes an existing transient atom.
    /// A transient atom is stamped with the current epoch, so it outlives the claimants
    /// that have not marked since (ADR-003).
    /// </summary>
    public static Atom Intern(string name, bool permanent = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (permanent)
        {
            if (_permanentByName.TryGetValue(name, out var perm))
                return perm;
        }
        else if (_byName.TryGetValue(name, out var liveRef)
                 && liveRef.TryGetTarget(out var alive)
                 && (alive.IsPermanent || Taken(alive)))
        {
            return alive;
        }
        lock (_lock)
        {
            if (_byName.TryGetValue(name, out var weakRef) && weakRef.TryGetTarget(out var existing)
                && Volatile.Read(ref existing.State) == 0)
            {
                if (permanent && !existing.IsPermanent)
                    PromoteToPermanentLocked(existing);
                else if (!existing.IsPermanent)
                {
                    Stamp(existing, Volatile.Read(ref _epoch));
                    if (_transientWeak.Remove(existing.Id))
                    {
                        _transientById[existing.Id] = existing;
                        StoreInArrayLocked(existing.Id, existing);
                    }
                }
                return existing;
            }

            int id = NextIdLocked();
            var atom = new Atom(id, name, isPermanent: permanent);
            atom.Epoch = Volatile.Read(ref _epoch);
            atom.NameRef = new WeakReference<Atom>(atom);
            if (permanent)
                _permanentById[id] = atom;
            else
                _transientById[id] = atom;
            // By id before by name: a lock-free intern that finds the name may
            // look the id up next.
            StoreInArrayLocked(id, atom);
            if (permanent)
                _permanentByName[name] = atom;
            _byName[name] = atom.NameRef;
            return atom;
        }
    }

    /// <summary>Takes an atom a holder kept again, as an intern of its name would:
    /// false when it died, and the holder then interns the name anew.</summary>
    public static bool Retake(Atom atom) => atom.IsPermanent || Taken(atom);

    // Stamps a transient atom taken now and tells whether it is alive. The stamp is a
    // full fence before the state is read, and Collect writes the state with a full
    // fence before it reads the epoch again: either Collect sees the stamp and keeps the
    // atom, or this sees the death and Intern makes a new atom under the lock.
    private static bool Taken(Atom atom)
    {
        Stamp(atom, Volatile.Read(ref _epoch));
        return Volatile.Read(ref atom.State) == 0;
    }

    // Raises an atom's epoch to at least `now`, always with an interlocked write. Not
    // a plain exchange: a stamp that read the global epoch earlier and lands later
    // would move the epoch back under a claimant that took the atom meanwhile.
    private static void Stamp(Atom atom, long now)
    {
        long cur;
        do
        {
            cur = Volatile.Read(ref atom.Epoch);
        } while (Interlocked.CompareExchange(ref atom.Epoch, cur > now ? cur : now, cur) != cur);
    }

    // A dead atom's id when one is free, else one never handed out.
    private static int NextIdLocked()
        => _freeCount > 0 ? _freeIds[--_freeCount] : _nextId++;

    // An id whose atom is gone from every table: free, or poisoned.
    private static void FreeIdLocked(int id, string name)
    {
        if (_poisonDeadIds)
        {
            _poisoned[id] = name;
            return;
        }
        if (_freeCount == _freeIds.Length)
            System.Array.Resize(ref _freeIds, System.Math.Max(64, _freeCount * 2));
        _freeIds[_freeCount++] = id;
    }

    /// <summary>Returns the atom with <paramref name="id"/>, or <c>null</c> if no live atom
    /// carries that id. Under <see cref="PoisonDeadIds"/>, a dead atom's id throws.</summary>
    public static Atom? GetById(int id)
    {
        Atom?[] arr = _byIdArray;
        if ((uint)id < (uint)arr.Length && arr[id] is { } atom)
            return atom;
        lock (_lock)
        {
            if (FindLocked(id) is { } found) return found;
            if (_poisoned.TryGetValue(id, out var dead))
            {
                PoisonedReads++;
                throw new InvalidOperationException(
                    $"Atom id {id} ('{dead}') was read after its atom died: a holder of it is not marked (ADR-003).");
            }
            return null;
        }
    }

    /// <summary>As <see cref="GetById"/> for an id that may be a dead atom's, such as
    /// the name of an entry of the functor table: null then, poisoned or not.</summary>
    public static Atom? ProbeById(int id)
    {
        Atom?[] arr = _byIdArray;
        if ((uint)id < (uint)arr.Length && arr[id] is { } atom)
            return atom;
        lock (_lock)
            return FindLocked(id);
    }

    private static Atom? FindLocked(int id)
    {
        if (_transientById.TryGetValue(id, out var transient)) return transient;
        if (_permanentById.TryGetValue(id, out var permanent)) return permanent;
        if (_transientWeak.TryGetValue(id, out var entry) && entry.Weak.TryGetTarget(out var weak))
            return weak;
        return null;
    }

    /// <summary>True when <paramref name="id"/> names a live transient atom: what a mark
    /// lists as a claim.</summary>
    public static bool IsTransient(int id)
    {
        Atom?[] arr = _byIdArray;
        if ((uint)id < (uint)arr.Length && arr[id] is { } atom)
            return !atom.IsPermanent;
        lock (_lock)
            return _transientById.ContainsKey(id)
                || (_transientWeak.TryGetValue(id, out var entry) && entry.Weak.TryGetTarget(out _));
    }

    /// <summary>
    /// Records that <paramref name="atom"/> has been exposed to C# code through the embedding
    /// API. While C# retains the reference, the atom is not collected even if no claimant
    /// holds it. No-op for permanent atoms.
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

    /// <summary>The current global epoch.</summary>
    public static long CurrentEpoch => Volatile.Read(ref _epoch);

    /// <summary>Whether claimants mark and collections run. Off until every holder of
    /// atom ids is a claimant: a collection frees what no claimant names.</summary>
    public static bool CollectionEnabled { get; set; }

    /// <summary>One past the highest id handed out: every id a cell can hold is below.</summary>
    public static int IdBound => Volatile.Read(ref _nextId);

    /// <summary>For tests: a dead atom's id is not handed out again, and
    /// <see cref="GetById"/> throws on it, so a holder the marks miss fails where it
    /// reads instead of reading another atom. Switched off, the ids poisoned so far
    /// are free.</summary>
    public static bool PoisonDeadIds
    {
        get { lock (_lock) return _poisonDeadIds; }
        set
        {
            lock (_lock)
            {
                _poisonDeadIds = value;
                if (value) return;
                foreach (var kv in _poisoned) FreeIdLocked(kv.Key, kv.Value);
                _poisoned.Clear();
            }
        }
    }

    /// <summary>Dead ids <see cref="GetById"/> was asked for under
    /// <see cref="PoisonDeadIds"/>, caught or not.</summary>
    internal static int PoisonedReads { get; private set; }

    /// <summary>Registers a claimant for <paramref name="owner"/>, which it references
    /// weakly. It takes nothing before this returns: its last mark is the epoch now,
    /// and every atom it interns from here is stamped at or after it.</summary>
    public static AtomClaimant RegisterClaimant(IAtomClaimantOwner owner)
    {
        lock (_lock)
        {
            var claimant = new AtomClaimant(owner, Volatile.Read(ref _epoch)) { Registered = true };
            _claimants.Add(claimant);
            return claimant;
        }
    }

    internal static void Unregister(AtomClaimant claimant)
    {
        lock (_lock)
        {
            if (!claimant.Registered) return;
            claimant.Registered = false;
            _claimants.Remove(claimant);
            claimant.ReleaseLocked();
        }
    }

    // The oldest last mark among the live claimants. One whose owner is gone is
    // released here: nothing can run it again.
    private static long MinLastMarkLocked()
    {
        long min = long.MaxValue;
        for (int i = _claimants.Count - 1; i >= 0; i--)
        {
            var claimant = _claimants[i];
            if (!claimant.Owner.TryGetTarget(out _))
            {
                claimant.Registered = false;
                _claimants.RemoveAt(i);
                claimant.ReleaseLocked();
                continue;
            }
            long mark = claimant.LastMark;
            if (mark < min) min = mark;
        }
        return min;
    }

    internal static int ClaimantCount { get { lock (_lock) return _claimants.Count; } }

    /// <summary>Starts a collection: every claimant marks, an idle one here on the
    /// calling thread and a running one at its next safe point or as it stops, and
    /// the last mark collects. A claimant that registers meanwhile is not waited for.</summary>
    public static void RequestCollection()
    {
        AtomClaimant[] claimants;
        lock (_lock)
        {
            if (_pendingTarget == 0) Volatile.Write(ref _pendingTarget, BeginMark());
            claimants = _claimants.ToArray();
        }
        foreach (var claimant in claimants) claimant.MarkOrAsk();
        CompletePendingCollection();
    }

    /// <summary>Whether a requested collection waits for a claimant whose last
    /// mark is <paramref name="lastMark"/>.</summary>
    internal static bool WaitsFor(long lastMark)
    {
        long target = Volatile.Read(ref _pendingTarget);
        return target != 0 && lastMark < target;
    }

    /// <summary>Collects once every claimant has marked since the requested
    /// collection began: called after each mark and each release.</summary>
    internal static void CompletePendingCollection()
    {
        if (Volatile.Read(ref _pendingTarget) == 0) return;
        lock (_lock)
        {
            long target = _pendingTarget;
            if (target == 0 || MinLastMarkLocked() < target) return;
            Volatile.Write(ref _pendingTarget, 0);
            CollectLocked(long.MaxValue);
        }
    }

    /// <summary>Whether a requested collection waits for a claimant to mark.</summary>
    public static bool CollectionPending => Volatile.Read(ref _pendingTarget) != 0;

    /// <summary>The transient atoms among the ids set in <paramref name="bits"/>,
    /// ascending, into <paramref name="found"/>; returns their count. An id of no live
    /// atom is dropped, and a TransientWeak atom counts: a claim takes it back.</summary>
    internal static int TransientAmong(ulong[] bits, int words, ref int[] found)
    {
        Atom?[] arr = _byIdArray;
        int n = 0;
        List<int>? missing = null;
        for (int w = 0; w < words; w++)
        {
            ulong word = bits[w];
            while (word != 0)
            {
                int id = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(word);
                word &= word - 1;
                if ((uint)id < (uint)arr.Length && arr[id] is { } atom)
                {
                    if (atom.IsPermanent) continue;
                    if (n == found.Length) System.Array.Resize(ref found, System.Math.Max(64, n * 2));
                    found[n++] = id;
                }
                else (missing ??= new()).Add(id);
            }
        }
        if (missing is null) return n;
        bool added = false;
        lock (_lock)
            foreach (int id in missing)
                if (_transientWeak.TryGetValue(id, out var entry) && entry.Weak.TryGetTarget(out _))
                {
                    if (n == found.Length) System.Array.Resize(ref found, System.Math.Max(64, n * 2));
                    found[n++] = id;
                    added = true;
                }
        if (added) System.Array.Sort(found, 0, n);
        return n;
    }

    /// <summary>Starts a claimant's mark and returns its epoch: once the claims are
    /// applied, the claimant publishes it as its last mark.</summary>
    public static long BeginMark() => Interlocked.Increment(ref _epoch);

    /// <summary>
    /// Replaces a claimant's claims. <paramref name="before"/> is the sorted list of
    /// transient atom ids its previous mark held, <paramref name="after"/> the sorted list
    /// this mark found. An atom found and not held gains a claim; one held and not found
    /// loses one, stamped first with the current epoch, so a claimant that took it from
    /// this one and has not marked yet still keeps it.
    /// </summary>
    public static void ApplyClaims(ReadOnlySpan<int> before, ReadOnlySpan<int> after)
    {
        long now = Volatile.Read(ref _epoch);
        int i = 0, j = 0;
        while (i < before.Length || j < after.Length)
        {
            if (j == after.Length || (i < before.Length && before[i] < after[j]))
            {
                if (TransientFor(before[i++]) is { } lost)
                {
                    Stamp(lost, now);
                    Interlocked.Decrement(ref lost.Claims);
                }
            }
            else if (i == before.Length || after[j] < before[i])
            {
                if (TransientFor(after[j++]) is { } gained)
                    Interlocked.Increment(ref gained.Claims);
            }
            else
            {
                i++;
                j++;
            }
        }
    }

    private static Atom? TransientFor(int id)
    {
        Atom? atom = GetById(id);
        return atom is { IsPermanent: false } ? atom : null;
    }

    /// <summary>
    /// Collects the transient atoms no claimant keeps: no claim, and an epoch below the
    /// oldest last mark among the live claimants, so every one of them has marked since
    /// the atom was last taken or let go. An atom C# still holds goes to TransientWeak;
    /// the others die. A TransientWeak atom that is claimed or taken again is transient
    /// again. Returns the atoms that died.
    /// </summary>
    public static int Collect() => Collect(long.MaxValue);

    /// <summary>As <see cref="Collect()"/>, with <paramref name="minMarkEpoch"/> as one
    /// more last mark: what a claimant outside the registry has not marked since.</summary>
    public static int Collect(long minMarkEpoch)
    {
        lock (_lock)
            return CollectLocked(minMarkEpoch);
    }

    private static int CollectLocked(long minMarkEpoch)
    {
        long registered = MinLastMarkLocked();
        if (registered < minMarkEpoch) minMarkEpoch = registered;
        SweepCount++;
        var foreignAlive = new HashSet<int>();
        for (int i = _foreignWeakRefs.Count - 1; i >= 0; i--)
        {
            if (_foreignWeakRefs[i].TryGetTarget(out var held))
                foreignAlive.Add(held.Id);
            else
                _foreignWeakRefs.RemoveAt(i);
        }

        List<Atom>? demote = null, candidates = null;
        foreach (var atom in _transientById.Values)
        {
            if (Volatile.Read(ref atom.Claims) != 0
                || Volatile.Read(ref atom.Epoch) >= minMarkEpoch)
                continue;
            if (foreignAlive.Contains(atom.Id)) (demote ??= new()).Add(atom);
            else (candidates ??= new()).Add(atom);
        }
        if (demote is not null)
            foreach (var atom in demote)
            {
                _transientById.Remove(atom.Id);
                StoreInArrayLocked(atom.Id, null);
                _transientWeak[atom.Id] = new TransientWeakEntry(
                    new WeakReference<Atom>(atom), atom.Name, atom.NameRef!);
            }
        int died = 0;
        if (candidates is not null)
            foreach (var atom in candidates)
            {
                // The state is written before the epoch is read again: an intern
                // that stamped the atom meanwhile is seen here, or sees the death.
                Interlocked.Exchange(ref atom.State, 1);
                if (Volatile.Read(ref atom.Epoch) >= minMarkEpoch
                    || Volatile.Read(ref atom.Claims) != 0)
                {
                    Volatile.Write(ref atom.State, 0);
                    continue;
                }
                _transientById.Remove(atom.Id);
                StoreInArrayLocked(atom.Id, null);
                RemoveNameLocked(atom.Name, atom.NameRef!);
                FreeIdLocked(atom.Id, atom.Name);
                died++;
            }
        SweptAtoms += died;

        List<int>? weakDropped = null;
        List<Atom>? weakBack = null;
        foreach (var kv in _transientWeak)
        {
            if (!kv.Value.Weak.TryGetTarget(out var atom))
                (weakDropped ??= new()).Add(kv.Key);
            else if (Volatile.Read(ref atom.Claims) != 0
                     || Volatile.Read(ref atom.Epoch) >= minMarkEpoch)
                (weakBack ??= new()).Add(atom);
        }
        if (weakDropped is not null)
            foreach (int id in weakDropped)
            {
                var entry = _transientWeak[id];
                _transientWeak.Remove(id);
                RemoveNameLocked(entry.Name, entry.NameRef);
                FreeIdLocked(id, entry.Name);
            }
        if (weakBack is not null)
            foreach (var atom in weakBack)
            {
                _transientWeak.Remove(atom.Id);
                _transientById[atom.Id] = atom;
                StoreInArrayLocked(atom.Id, atom);
            }
        return died;
    }

    // Removes a name's entry only while it is still the given atom's: the name may name
    // a newer atom by now.
    private static void RemoveNameLocked(string name, WeakReference<Atom> nameRef)
        => ((ICollection<KeyValuePair<string, WeakReference<Atom>>>)_byName)
            .Remove(new KeyValuePair<string, WeakReference<Atom>>(name, nameRef));

    private static void PromoteToPermanentLocked(Atom atom)
    {
        if (atom.IsPermanent)
            return;
        atom.IsPermanent = true;
        _permanentById[atom.Id] = atom;
        _transientById.Remove(atom.Id);
        _transientWeak.Remove(atom.Id);
        StoreInArrayLocked(atom.Id, atom);
        _permanentByName[atom.Name] = atom;
    }

    // ---------- Diagnostics / test helpers ----------

    /// <summary>Collections run and transient atoms they reclaimed, process-wide
    /// like the table itself; statistics/0 reports both.</summary>
    public static int SweepCount { get; private set; }
    public static long SweptAtoms { get; private set; }

    /// <summary>Occupancy of the two liveness tiers; statistics/0's atom line reads them.</summary>
    public static int PermanentCount { get { lock (_lock) return _permanentById.Count; } }
    public static int TransientCount { get { lock (_lock) return _transientById.Count; } }
    internal static int TransientWeakCount { get { lock (_lock) return _transientWeak.Count; } }
    internal static int FreeIdCount { get { lock (_lock) return _freeCount; } }
    internal static int ForeignHoldCount { get { lock (_lock) return _foreignWeakRefs.Count; } }

    /// <summary>Claims currently counted on <paramref name="id"/>, -1 for a permanent
    /// or unknown atom. For tests and diagnostics.</summary>
    public static int ClaimsOf(int id) => TransientFor(id) is { } a ? Volatile.Read(ref a.Claims) : -1;

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
            foreach (var claimant in _claimants) claimant.Registered = false;
            _claimants.Clear();
            _pendingTarget = 0;
            _byIdArray = System.Array.Empty<Atom?>();
            _permanentByName.Clear();
            _nextId = FirstUserId;
            _freeCount = 0;
            _poisoned.Clear();
            _poisonDeadIds = false;
            PoisonedReads = 0;
            _epoch = 0;
            AddPreRegisteredLocked(EmptyListId, "[]");
            AddPreRegisteredLocked(EmptyBracesId, "{}");
            AddPreRegisteredLocked(ConsFunctorId, ".");
            AddPreRegisteredLocked(TrueId, "true");
            AddPreRegisteredLocked(FalseId, "false");
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
            int id = NextIdLocked();
            var atom = new Atom(id, s, isPermanent: true);
            atom.NameRef = new WeakReference<Atom>(atom);
            _byName[s] = atom.NameRef;
            _permanentById[id] = atom;
            _permanentByName[s] = atom;
            StoreInArrayLocked(id, atom);
            _singleCharAtomIds[i] = id;
        }
    }
}
