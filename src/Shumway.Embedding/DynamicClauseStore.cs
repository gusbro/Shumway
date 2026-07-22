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
    public IReadOnlySet<int> Functors => _functors;

    public bool IsDynamic(int fid) => _functors.Contains(fid);
    public bool MarkDynamic(int fid) => _functors.Add(fid);
    public bool UnmarkDynamic(int fid) => _functors.Remove(fid);
    public void MarkDynamicAll(IEnumerable<int> fids) => _functors.UnionWith(fids);
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
        set => _clauses[fid] = value;
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

    public bool RemoveSlot(int fid) => _clauses.Remove(fid);
    public void ClearAllSlots() => _clauses.Clear();
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

    // ----- retract snapshot pool -----
    // retract/1 walks a snapshot of the clause list so mid-walk mutation
    // can't skew it; the buffer is pooled (one spare) because the classic
    // Edinburgh drain retracts once per element.

    private Clause[]? _retractSnapshotSpare;
    private const int RetractSnapshotSpareMaxLen = 4096;

    public Clause[] RentRetractSnapshot(int minLength)
    {
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
