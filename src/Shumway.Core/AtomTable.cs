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

    private static readonly Dictionary<string, WeakReference<Atom>> _byName = new();
    private static readonly Dictionary<int, Atom> _permanentById = new();
    private static readonly Dictionary<int, Atom> _transientById = new();
    private static readonly Dictionary<int, TransientWeakEntry> _transientWeak = new();
    private static readonly List<WeakReference<Atom>> _foreignWeakRefs = new();
    private static readonly object _lock = new();
    private static int _nextId = FirstUserId;

    private readonly struct TransientWeakEntry
    {
        public readonly WeakReference<Atom> Weak;
        public readonly string Name;
        public TransientWeakEntry(WeakReference<Atom> weak, string name) { Weak = weak; Name = name; }
    }

    static AtomTable()
    {
        AddPreRegisteredLocked(EmptyListId, "[]");
        AddPreRegisteredLocked(EmptyBracesId, "{}");
        AddPreRegisteredLocked(ConsFunctorId, ".");
        AddPreRegisteredLocked(TrueId, "true");
        AddPreRegisteredLocked(FalseId, "false");
    }

    private static void AddPreRegisteredLocked(int id, string name)
    {
        var atom = new Atom(id, name, isPermanent: true);
        _byName[name] = new WeakReference<Atom>(atom);
        _permanentById[id] = atom;
    }

    /// <summary>
    /// Returns the canonical <see cref="Atom"/> for <paramref name="name"/>, allocating one
    /// on first use. Passing <c>permanent: true</c> promotes an existing transient atom.
    /// </summary>
    public static Atom Intern(string name, bool permanent = false)
    {
        ArgumentNullException.ThrowIfNull(name);
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
                _permanentById[id] = atom;
            else
                _transientById[id] = atom;
            return atom;
        }
    }

    /// <summary>Returns the atom with <paramref name="id"/>, or <c>null</c> if no live atom carries that id.</summary>
    public static Atom? GetById(int id)
    {
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
                _byName.Remove(kv.Value.Name);
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
                _byName.Remove(kv.Value);
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
            _nextId = FirstUserId;
            AddPreRegisteredLocked(EmptyListId, "[]");
            AddPreRegisteredLocked(EmptyBracesId, "{}");
            AddPreRegisteredLocked(ConsFunctorId, ".");
            AddPreRegisteredLocked(TrueId, "true");
            AddPreRegisteredLocked(FalseId, "false");
        }
    }
}
