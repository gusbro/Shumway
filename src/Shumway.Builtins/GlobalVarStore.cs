using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Per-engine name → cell store backing SWI's <c>nb_setval</c> /
/// <c>nb_getval</c> family. Two kinds of binding:
///
/// <list type="bullet">
/// <item><b>Non-backtrackable</b> (<c>nb_setval/2</c>): the write
///   persists across backtracking. The store just records the
///   latest cell value.</item>
/// <item><b>Backtrackable</b> (<c>b_setval/2</c> / Scryer's
///   <c>bb_b_put/2</c>): the builtin trails the previous value via
///   <c>Activation.TrailExternal</c> before writing; unwinding calls
///   <see cref="RestoreExternal"/> to put it back (or remove the key
///   the write created).</item>
/// </list>
///
/// <para>Cells stored here are snapshots taken at write time —
/// see <c>Activation.SnapshotIntoHeap</c>. The store survives across
/// queries on the hosting engine.</para>
///
/// <para>Keyed by ATOM ID, not name string — the id is what the builtin
/// already has in hand, and keying by string paid a name lookup plus a
/// string hash per access on hot nb_getval loops. Atom ids are stable
/// for the lifetime of the atom (ADR-003); the lifetime itself is
/// guaranteed by <see cref="_retained"/>, which holds a strong reference
/// to each key's <see cref="AtomTable"/> entry so a transient-tier name
/// can't be collected (and its id reused) while a global var is keyed
/// by it.</para>
/// </summary>
public sealed class GlobalVarStore : IExternalTrailTarget
{
    private readonly Dictionary<int, Cell> _byAtomId = new();
    private readonly Dictionary<int, object> _retained = new();

    public void Set(int atomId, Cell value, bool backtrackable)
    {
        // Backtrackability is the CALLER's concern: b_setval trails the
        // previous value (Activation.TrailExternal) before calling here.
        _ = backtrackable;
        if (!_retained.ContainsKey(atomId))
        {
            object? entry = AtomTable.GetById(atomId);
            if (entry is not null) _retained[atomId] = entry;
        }
        _byAtomId[atomId] = value;
    }

    /// <summary>Backtracking undo for a trailed <c>b_setval</c> write.</summary>
    public void RestoreExternal(int key, Cell oldValue, bool hadOldValue)
    {
        if (hadOldValue) _byAtomId[key] = oldValue;
        else _byAtomId.Remove(key);
    }

    public bool TryGet(int atomId, out Cell value) =>
        _byAtomId.TryGetValue(atomId, out value);

    public IEnumerable<(string Name, Cell Value)> All() =>
        _byAtomId.Select(p =>
            (AtomTable.GetById(p.Key)?.Name ?? "", p.Value));

    /// <summary>ADR-016 — rewrites every stored cell through the heap
    /// collector's relocation map. A no-op for value-carrying cells
    /// (Int/Atom/Float/BigInt/Foreign carry no heap index); cells that
    /// reference the heap (Str/Lis/Pstr) get their payload remapped so
    /// they survive a mid-query compaction.</summary>
    public void RelocateCells(System.Func<Cell, Cell> reloc)
    {
        foreach (var key in _byAtomId.Keys.ToList())
            _byAtomId[key] = reloc(_byAtomId[key]);
    }
}
