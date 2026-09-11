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
    public bool TryGetClauses(int fid, out List<Clause> clauses)
        => _clauses.TryGetValue(fid, out clauses!);

    /// <summary>The live clause list for <paramref name="fid"/> (get), or
    /// replaces the slot outright (set). Get throws when absent — use
    /// <see cref="Slot"/> for get-or-create.</summary>
    public List<Clause> this[int fid]
    {
        get => _clauses[fid];
        set { NotifyReplaced(fid); _clauses[fid] = value; }
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
        return list;
    }

    public bool RemoveSlot(int fid)
    {
        NotifyReplaced(fid);
        return _clauses.Remove(fid);
    }

    public void ClearAllSlots()
    {
        foreach (int fid in _clauses.Keys) NotifyReplaced(fid);
        _clauses.Clear();
    }
    public IEnumerable<int> ClauseFunctors => _clauses.Keys;
    public int ClauseFunctorCount => _clauses.Count;
    public IEnumerable<KeyValuePair<int, List<Clause>>> Slots => _clauses;

    /// <summary>Deep-copies this store's contents into
    /// <paramref name="target"/> (fresh clause lists; shared Clause objects —
    /// clauses are immutable ASTs). Used by sub-engine creation.</summary>
    public void CopyInto(DynamicClauseStore target)
    {
        target._functors.UnionWith(_functors);
        foreach (var (fid, clauses) in _clauses)
            target._clauses[fid] = new List<Clause>(clauses);
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
        /// <summary>A clause is about to be removed at <paramref name="index"/>.
        /// Returns after the window has either shifted or materialized.</summary>
        void BeforeRemoveAt(int index);
        /// <summary>A clause is about to be inserted at <paramref name="index"/>.</summary>
        void BeforeInsertAt(int index);
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
    }

    public void PrependClause(int fid, Clause c)
    {
        NotifyInsertAt(fid, 0);
        Slot(fid).Insert(0, c);
    }

    public void RemoveClauseAt(int fid, int index)
    {
        NotifyRemoveAt(fid, index);
        _clauses[fid].RemoveAt(index);
    }

    public void ClearClauses(int fid)
    {
        if (!_clauses.TryGetValue(fid, out var list)) return;
        NotifyReplaced(fid);
        list.Clear();
    }

    /// <summary>Replaces the clause at <paramref name="index"/> (consult-time
    /// goal expansion rewrites a just-stored clause). A window holding that
    /// position would otherwise silently change identity, so it copies out.</summary>
    public void ReplaceClauseAt(int fid, int index, Clause c)
    {
        NotifyReplaced(fid);
        _clauses[fid][index] = c;
    }

    /// <summary>Bulk load into a (re)built slot — consult, bundle load,
    /// restore. Windows cannot track this, so they copy out first.</summary>
    public void AppendClauseRange(int fid, IEnumerable<Clause> cs)
    {
        NotifyReplaced(fid);
        Slot(fid).AddRange(cs);
    }

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
