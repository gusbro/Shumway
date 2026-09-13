using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>
/// The dynamic-predicate clause store: which functors are dynamic, and the
/// ordered clause list each one currently holds. Owned by a
/// <c>PrologEngine</c>; the bytecode-level machinery (trampolines, in-place
/// chain mutation, snapshots) stays in the engine and consults this store as
/// its source of truth.
///
/// <para>Invariant: every functor with a clause slot is also marked dynamic
/// (<see cref="Functors"/> ⊇ slot keys). The reverse does not hold — a
/// <c>:- dynamic</c> declaration with no clauses yet has a mark and no
/// slot.</para>
///
/// <para><see cref="Functors"/> is the LIVE set, exposed read-only: consumers
/// (module rewrite contexts, the module compiler) hold the reference and see
/// later marks — the sharing the compile pipeline relies on. Mutations go
/// through the store. Not thread-safe on its own; access is serialized by the
/// owning engine's contract.</para>
/// </summary>
internal sealed class DynamicClauseStore
{
    private readonly Dictionary<int, List<Clause>> _clauses = new();
    private readonly HashSet<int> _functors = new();

    // ----- tombstones -----
    //
    // Removing a clause from an array-backed list shifts everything above it,
    // so draining a big predicate from the head is quadratic in its size. A
    // retract therefore leaves a TOMBSTONE in the slot and the slot closes up
    // later, in a compaction -- the same shape as the chain's dead chunks and
    // the linker's dead regions.
    //
    // The tombstones are STRICTLY INTERNAL. Every reader compacts the slot
    // before looking, so outside this class the list is always dense and no
    // consumer's idea of a clause position changes; the only code that walks
    // over a tombstone is the retract path, which asks for the physical list
    // explicitly and skips them by reference. Compacting on read is what
    // makes that sound: between reads only retract runs, and at every read
    // the state collapses to exactly what eager removal produced.
    //
    // Compaction is PROPORTIONAL, not periodic: compacting every K retracts
    // is n/K passes of O(n), still quadratic. Compacting when tombstones
    // reach half the slot makes each pass pay for the retracts that caused
    // it -- amortised O(1) -- and keeps a never-returning query compacting.

    /// <summary>A retracted clause's slot, compared by reference. It never
    /// leaves this class except through <see cref="PhysicalClauses"/>.</summary>
    internal static readonly Clause Tombstone =
        Clause.From(new AtomTerm("$$retracted_clause"));

    private readonly Dictionary<int, int> _tombstones = new();

    // Fenwick tree over tombstone positions, one per tombstoned slot. The
    // chain's entries stay dense (a retracted entry leaves it), so handing
    // the chain a PHYSICAL position as a hint needs the translation
    // dense = physical - tombstonesBefore(physical), and computing that by
    // scanning would put the walk right back. O(log n) per tombstone and per
    // query; gone when the slot compacts. asserta compacts first, because a
    // front insertion would shift every rank and it already pays O(n) for
    // the list insertion itself.
    private readonly Dictionary<int, int[]> _tombRank = new();

    private void RankAdd(int fid, int index, int physicalCount)
    {
        if (!_tombRank.TryGetValue(fid, out var t) || t.Length < physicalCount + 1)
        {
            var grown = new int[System.Math.Max(physicalCount + 1, 16)];
            if (t is not null) RebuildRankInto(grown, t);
            _tombRank[fid] = t = grown;
        }
        for (int i = index + 1; i < t.Length; i += i & -i) t[i]++;
    }

    private static void RebuildRankInto(int[] grown, int[] old)
    {
        // Recover each position's count from the old tree, replay into the
        // new one. Runs only on growth, which appends amortise away.
        for (int i = 1; i < old.Length; i++)
        {
            int count = old[i];
            for (int j = i - 1; j > i - (i & -i); j -= j & -j) count -= old[j];
            if (count == 0) continue;
            for (int k = i; k < grown.Length; k += k & -k) grown[k] += count;
        }
    }

    /// <summary>Tombstoned slots at positions strictly below
    /// <paramref name="index"/> — the correction that turns a physical slot
    /// position into the dense one every structure outside this class uses.</summary>
    public int TombstonesBefore(int fid, int index)
    {
        if (!_tombRank.TryGetValue(fid, out var t)) return 0;
        int sum = 0;
        for (int i = System.Math.Min(index, t.Length - 1); i > 0; i -= i & -i)
            sum += t[i];
        return sum;
    }

    private const int CompactWhenOneIn = 2;

    public int TombstoneCount(int fid)
        => _tombstones.TryGetValue(fid, out int n) ? n : 0;

    /// <summary>Slot compactions performed — the shifts the tombstones defer.</summary>
    public long Compactions;

    /// <summary>The list as stored, tombstones included. Retract only: it is
    /// the one caller that must not pay a compaction per call, and the one
    /// that knows to skip a tombstone.</summary>
    public List<Clause> PhysicalClauses(int fid) => _clauses[fid];

    /// <summary>Closes up every tombstone in the slot, in place — the held
    /// list reference stays the live list. Positions move here and only here,
    /// so any open retract window copies itself out first.</summary>
    public void CompactSlot(int fid)
    {
        if (TombstoneCount(fid) == 0) return;
        if (!_clauses.TryGetValue(fid, out var list)) return;
        NotifyCompacting(fid);
        int w = 0;
        for (int r = 0; r < list.Count; r++)
            if (!ReferenceEquals(list[r], Tombstone)) list[w++] = list[r];
        list.RemoveRange(w, list.Count - w);
        _tombstones.Remove(fid);
        _tombRank.Remove(fid);
        Compactions++;
        if (_indexes.TryGetValue(fid, out var ix)) ix.Rebuild(list);
    }

    public void CompactAllSlots()
    {
        if (_tombstones.Count == 0) return;
        foreach (int fid in new List<int>(_tombstones.Keys)) CompactSlot(fid);
    }

    private List<Clause> Dense(int fid, List<Clause> list)
    {
        if (TombstoneCount(fid) != 0) CompactSlot(fid);
        return list;
    }

    // ----- dynamic marks -----

    /// <summary>Live read-only view of every functor marked dynamic.</summary>
    public ISet<int> Functors => _functors;

    public bool IsDynamic(int fid) => _functors.Contains(fid);
    public bool MarkDynamic(int fid)
    { Abolished.Remove(fid); ImplicitOnly.Remove(fid); return _functors.Add(fid); }
    public bool UnmarkDynamic(int fid) => _functors.Remove(fid);
    public void MarkDynamicAll(IEnumerable<int> fids)
    { Abolished.ExceptWith(fids); ImplicitOnly.ExceptWith(fids); _functors.UnionWith(fids); }

    /// <summary>Tombstones left by abolish/1: dispatching one of these
    /// raises existence_error (the predicate is UNDEFINED) instead of
    /// failing over its dead chain. Re-marking dynamic clears the
    /// tombstone.</summary>
    public readonly HashSet<int> Abolished = new();

    /// <summary>Functors marked dynamic ONLY by the implicit_dynamic
    /// consult-time scan — a literal <c>assertz(Head)</c> was seen in some
    /// clause body, so the linker must emit a real trampoline for calls to
    /// Head, but nothing has DECLARED or asserted it yet.
    ///
    /// <para>Such a predicate is not yet in the database: current_predicate/1
    /// does not enumerate it and calling it goes through the <c>unknown</c>
    /// flag like any other undefined procedure — which is what GNU Prolog,
    /// SWI and Scryer all do, and what §8.8.2.1 says. A real
    /// <c>:- dynamic</c> declaration or the first assert clears the mark and
    /// it becomes an ordinary dynamic (empty chain FAILS, and it
    /// enumerates).</para></summary>
    public readonly HashSet<int> ImplicitOnly = new();

    /// <summary>True when the scan is all that knows about this functor.</summary>
    public bool IsImplicitOnly(int fid) => ImplicitOnly.Contains(fid);

    /// <summary>Promotes an implicit mark to a real one — called when the
    /// predicate is declared or actually gets a clause.</summary>
    public void ClearImplicitOnly(int fid) => ImplicitOnly.Remove(fid);
    public int FunctorCount => _functors.Count;

    // ----- clause slots -----

    public bool HasClauses(int fid) => _clauses.ContainsKey(fid);

    /// <summary>The predicate's clauses, dense: a slot with tombstones is
    /// compacted before it is handed out, so no reader can meet one.</summary>
    public bool TryGetClauses(int fid, out List<Clause> clauses)
    {
        if (!_clauses.TryGetValue(fid, out clauses!)) return false;
        clauses = Dense(fid, clauses);
        return true;
    }

    /// <summary>The live clause list for <paramref name="fid"/> (get), or
    /// replaces the slot outright (set). Get throws when absent — use
    /// <see cref="Slot"/> for get-or-create.</summary>
    public List<Clause> this[int fid]
    {
        get => Dense(fid, _clauses[fid]);
        set
        {
            NotifyReplaced(fid);
            _indexes.Remove(fid);
            _tombstones.Remove(fid);
            _tombRank.Remove(fid);
            _clauses[fid] = value;
        }
    }

    /// <summary>Get-or-create: the live clause list for
    /// <paramref name="fid"/>, creating an empty slot (and the dynamic mark)
    /// on first use.</summary>
    public List<Clause> Slot(int fid)
    {
        if (!_clauses.TryGetValue(fid, out var list))
        {
            list = new List<Clause>();
            _clauses[fid] = list;
            _functors.Add(fid);
        }
        return Dense(fid, list);
    }

    public bool RemoveSlot(int fid)
    {
        NotifyReplaced(fid);
        _indexes.Remove(fid);
        _tombstones.Remove(fid);
        _tombRank.Remove(fid);
        return _clauses.Remove(fid);
    }

    public void ClearAllSlots()
    {
        foreach (int fid in _clauses.Keys) NotifyReplaced(fid);
        _clauses.Clear();
        _indexes.Clear();
        _tombstones.Clear();
        _tombRank.Clear();
    }
    public IEnumerable<int> ClauseFunctors => _clauses.Keys;
    public int ClauseFunctorCount => _clauses.Count;
    public IEnumerable<KeyValuePair<int, List<Clause>>> Slots
    {
        get
        {
            foreach (var (fid, list) in _clauses)
                yield return new KeyValuePair<int, List<Clause>>(
                    fid, Dense(fid, list));
        }
    }

    /// <summary>Deep-copies this store's contents into
    /// <paramref name="target"/> (fresh clause lists; shared Clause objects —
    /// clauses are immutable ASTs). Used by sub-engine creation.</summary>
    public void CopyInto(DynamicClauseStore target)
    {
        target._functors.UnionWith(_functors);
        foreach (var (fid, clauses) in _clauses)
            target._clauses[fid] = new List<Clause>(Dense(fid, clauses));
    }

    // ----- open retract enumerations -----

    /// <summary>A retract/1 enumeration that has not copied its remaining
    /// candidates yet. It holds a window [start, end) of the LIVE clause
    /// list instead, and the store tells it about mutations so it can either
    /// adjust the window or copy the window out before it is disturbed.
    ///
    /// <para>The window is the enumeration's ISO logical-update view, so a
    /// clause removed from inside it must be COPIED OUT first: the view
    /// still contains it. Everything outside the window is free.</para></summary>
    internal interface IClauseWindow
    {
        /// <summary>The clause at <paramref name="index"/> is about to become
        /// a tombstone. Its position does not move, so a window materializes
        /// only when the slot is INSIDE it -- the view keeps the clause, and
        /// the copy runs while it is still there.</summary>
        void BeforeRemoveAt(int index);
        /// <summary>A clause is about to be inserted at <paramref name="index"/>.</summary>
        void BeforeInsertAt(int index);
        /// <summary>The slot is about to compact: every position falls by the
        /// number of tombstones below it, and the window can follow that with
        /// two subtractions instead of copying itself out. The store is still
        /// pre-compaction when this runs, so the ranks are still there to
        /// ask.</summary>
        void BeforeCompact(DynamicClauseStore store, int fid);

        /// <summary>The list is about to change in a way the window cannot
        /// track (cleared, replaced, bulk-loaded): copy the window out.</summary>
        void Materialize();
    }

    private readonly Dictionary<int, List<IClauseWindow>> _windows = new();

    public void OpenWindow(int fid, IClauseWindow w)
    {
        if (!_windows.TryGetValue(fid, out var list))
            _windows[fid] = list = new List<IClauseWindow>();
        list.Add(w);
    }

    public void CloseWindow(int fid, IClauseWindow w)
    {
        if (_windows.TryGetValue(fid, out var list) && list.Remove(w)
            && list.Count == 0)
            _windows.Remove(fid);
    }

    /// <summary>Mutation chokepoint. Every change to a clause list goes
    /// through one of these three so no open window can be disturbed behind
    /// its back; the fast path is a dictionary miss.</summary>
    // A window that copies out closes itself, which removes it from `ws`
    // mid-notification: walk DOWNWARDS so a removal at or above the cursor
    // cannot make the walk skip an entry, and never copy the list (this runs
    // on every assert and retract).
    private void NotifyRemoveAt(int fid, int index)
    {
        if (_windows.Count == 0 || !_windows.TryGetValue(fid, out var ws)) return;
        for (int i = ws.Count - 1; i >= 0; i--)
        {
            if (i >= ws.Count) continue;
            ws[i].BeforeRemoveAt(index);
        }
    }

    private void NotifyInsertAt(int fid, int index)
    {
        if (_windows.Count == 0 || !_windows.TryGetValue(fid, out var ws)) return;
        for (int i = ws.Count - 1; i >= 0; i--)
        {
            if (i >= ws.Count) continue;
            ws[i].BeforeInsertAt(index);
        }
    }

    private void NotifyCompacting(int fid)
    {
        if (_windows.Count == 0 || !_windows.TryGetValue(fid, out var ws)) return;
        for (int i = ws.Count - 1; i >= 0; i--)
        {
            if (i >= ws.Count) continue;
            ws[i].BeforeCompact(this, fid);
        }
    }

    private void NotifyReplaced(int fid)
    {
        if (_windows.Count == 0 || !_windows.TryGetValue(fid, out var ws)) return;
        // Every window copies out, so `ws` empties and the dictionary entry
        // goes with it; take the last one each time rather than indexing.
        while (ws.Count > 0)
        {
            var w = ws[ws.Count - 1];
            w.Materialize();
            if (ws.Count > 0 && ReferenceEquals(ws[ws.Count - 1], w))
                ws.RemoveAt(ws.Count - 1);
        }
    }

    // ----- clause mutation (the only sanctioned way to change a list) -----

    public void AppendClause(int fid, Clause c)
    {
        var list = Slot(fid);
        NotifyInsertAt(fid, list.Count);
        list.Add(c);
        if (_indexes.TryGetValue(fid, out var ix)) ix.Append(c);
    }

    public void PrependClause(int fid, Clause c)
    {
        // Dense first: a front insertion shifts every position, which the
        // rank tree cannot follow — and Slot() compacts on read anyway. The
        // list insertion below is O(n) itself, so this costs nothing extra.
        var slot = Slot(fid);
        NotifyInsertAt(fid, 0);
        slot.Insert(0, c);
        if (_indexes.TryGetValue(fid, out var ix)) ix.Prepend(c);
    }

    /// <summary>Retires the clause at <paramref name="index"/> (a PHYSICAL
    /// position). The slot becomes a tombstone, so no position moves: a
    /// window is told first only when the slot is INSIDE it, because its view
    /// must keep the clause and the copy has to happen while the clause is
    /// still there. Half the slot tombstoned triggers the compaction.</summary>
    public void RemoveClauseAt(int fid, int index)
    {
        NotifyRemoveAt(fid, index);
        var list = _clauses[fid];
        Clause c = list[index];
        list[index] = Tombstone;
        int tombs = TombstoneCount(fid) + 1;
        _tombstones[fid] = tombs;
        RankAdd(fid, index, list.Count);
        if (_indexes.TryGetValue(fid, out var ix)) ix.MarkDead(index, c);
        if (tombs * CompactWhenOneIn >= list.Count) CompactSlot(fid);
    }

    public void ClearClauses(int fid)
    {
        if (!_clauses.TryGetValue(fid, out var list)) return;
        NotifyReplaced(fid);
        list.Clear();
        _indexes.Remove(fid);
        _tombstones.Remove(fid);
        _tombRank.Remove(fid);
    }

    /// <summary>Replaces the clause at <paramref name="index"/> (consult-time
    /// goal expansion rewrites a just-stored clause). A window holding that
    /// position would otherwise silently change identity, so it copies out.</summary>
    public void ReplaceClauseAt(int fid, int index, Clause c)
    {
        NotifyReplaced(fid);
        // The caller's position is dense (it read the slot to get it), so the
        // slot must be dense before the write lands.
        CompactSlot(fid);
        _clauses[fid][index] = c;
        // A clause swapped in place can carry a different key; rebuilding is
        // cheaper to be sure of than patching, and this is consult-time only.
        _indexes.Remove(fid);
    }

    /// <summary>Bulk load into a (re)built slot — consult, bundle load,
    /// restore. Windows cannot track this, so they copy out first.</summary>
    public void AppendClauseRange(int fid, IEnumerable<Clause> cs)
    {
        NotifyReplaced(fid);
        Slot(fid).AddRange(cs);
        _indexes.Remove(fid);
    }

    // ----- first-argument index -----

    private readonly Dictionary<int, DynamicClauseIndex> _indexes = new();

    /// <summary>The first-argument index for <paramref name="fid"/>, built on
    /// first use and maintained by the mutators above.
    ///
    /// <para>It is rebuilt when it disagrees with the list on how many clauses
    /// there are. That can only happen if something mutated a clause list
    /// without going through this class, which is a bug — but a stale index
    /// would silently drop solutions, and rebuilding costs one pass, so the
    /// index heals rather than lies.</para></summary>
    public DynamicClauseIndex IndexFor(int fid, List<Clause> clauses)
    {
        if (!_indexes.TryGetValue(fid, out var ix))
        {
            _indexes[fid] = ix = new DynamicClauseIndex();
            ix.Rebuild(clauses);
            return ix;
        }
        if (ix.Count != clauses.Count)
        {
            System.Diagnostics.Debug.Assert(false,
                "a clause list was mutated outside DynamicClauseStore");
            IndexRebuilds++;
            ix.Rebuild(clauses);
        }
        return ix;
    }

    /// <summary>Rebuilds forced by an index that had fallen out of step — zero
    /// unless a mutation escaped the chokepoint.</summary>
    public long IndexRebuilds;

    // ----- retract snapshot pool -----
    // retract/1 walks a snapshot of the clause list so mid-walk mutation
    // can't skew it; the buffer is pooled (one spare) because the classic
    // Edinburgh drain retracts once per element.

    private Clause[]? _retractSnapshotSpare;
    private const int RetractSnapshotSpareMaxLen = 4096;

    /// <summary>Total clauses ever copied out of a live list because a
    /// retract enumeration's window was about to be disturbed. The cost the
    /// window exists to avoid, counted exactly rather than timed.</summary>
    public long ClausesCopiedOut;

    public Clause[] RentRetractSnapshot(int minLength)
    {
        ClausesCopiedOut += minLength;
        Clause[]? spare = _retractSnapshotSpare;
        if (spare is not null && spare.Length >= minLength)
        {
            _retractSnapshotSpare = null;
            return spare;
        }
        return new Clause[minLength];
    }

    public void ReturnRetractSnapshot(Clause[] buffer, int usedCount)
    {
        Array.Clear(buffer, 0, usedCount);
        if (buffer.Length > RetractSnapshotSpareMaxLen) return;
        Clause[]? spare = _retractSnapshotSpare;
        if (spare is null || spare.Length < buffer.Length)
            _retractSnapshotSpare = buffer;
    }
}
