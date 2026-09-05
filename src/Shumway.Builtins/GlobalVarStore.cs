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
/// <para>Two shelves, and a key lives on exactly one of them. A
/// BACKTRACKABLE write stores the live cell: the write is undone by the
/// trail, so it cannot outlive the heap state it was taken from, and the
/// sharing is what a propagation queue driven through
/// <c>bb_b_put/2</c> relies on. A NON-backtrackable write stores a
/// heap-independent PAYLOAD instead (<see cref="SetPayload"/>) — it has to
/// survive the backtracking it is defined to survive, and a raw cell does
/// not: once the heap unwinds past the write, the address holds whatever
/// came later. An immediate (an integer, an atom) is its own payload and
/// stays on the cell shelf, which is the accumulator path and allocates
/// nothing.</para>
///
/// <para>The store survives across queries on the hosting engine.</para>
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
    private readonly Dictionary<int, object> _payloads = new();
    private readonly Dictionary<int, int> _owner = new();
    private readonly Dictionary<int, object> _retained = new();

    /// <summary>A backtrackable write keeps the live cell, so it is only
    /// meaningful while the heap it points into is the one being run. The
    /// owning activation is recorded and the read checks it: a query is a
    /// fresh activation, and the assignment a query made is over when the
    /// query is (the same place SWI's b_setval leaves it, since its toplevel
    /// backtracks out of the assignment).</summary>
    public void Set(int atomId, Cell value, bool backtrackable, int ownerId = 0)
    {
        Retain(atomId);
        _payloads.Remove(atomId);
        _byAtomId[atomId] = value;
        if (backtrackable) _owner[atomId] = ownerId;
        else _owner.Remove(atomId);
    }

    /// <summary>False when the key holds a backtrackable write from another
    /// activation; the entry is dropped on the way out, so the key reads as
    /// unset rather than as a term of a heap that is gone.</summary>
    public bool IsLiveFor(int atomId, int ownerId)
    {
        if (!_owner.TryGetValue(atomId, out int owner) || owner == ownerId) return true;
        _byAtomId.Remove(atomId);
        _owner.Remove(atomId);
        return false;
    }

    /// <summary>The non-backtrackable write of a value no cell can carry
    /// across the unwind: the payload is a heap-independent image the caller
    /// re-emits at read time.</summary>
    public void SetPayload(int atomId, object payload)
    {
        Retain(atomId);
        _byAtomId.Remove(atomId);
        _owner.Remove(atomId);
        _payloads[atomId] = payload;
    }

    public bool TryGetPayload(int atomId, out object payload) =>
        _payloads.TryGetValue(atomId, out payload!);

    private void Retain(int atomId)
    {
        if (_retained.ContainsKey(atomId)) return;
        object? entry = AtomTable.GetById(atomId);
        if (entry is not null) _retained[atomId] = entry;
    }

    /// <summary>Backtracking undo for a trailed <c>b_setval</c> write. A
    /// payload the write displaced is not restored here: the write removed
    /// the key from the cell shelf, so removing it again leaves the key
    /// unset, which is what the trail recorded.</summary>
    public void RestoreExternal(int key, Cell oldValue, bool hadOldValue)
    {
        if (hadOldValue) _byAtomId[key] = oldValue;
        else { _byAtomId.Remove(key); _owner.Remove(key); }
    }

    public bool TryGet(int atomId, out Cell value) =>
        _byAtomId.TryGetValue(atomId, out value);

    public IEnumerable<(string Name, Cell Value)> All() =>
        _byAtomId.Select(p =>
            (AtomTable.GetById(p.Key)?.Name ?? "", p.Value));

    /// <summary>Every key the store holds, on either shelf.</summary>
    public IEnumerable<int> Keys => _byAtomId.Keys.Concat(_payloads.Keys);

    public bool Has(int atomId) =>
        _byAtomId.ContainsKey(atomId) || _payloads.ContainsKey(atomId);

    public void Remove(int atomId)
    {
        _byAtomId.Remove(atomId);
        _payloads.Remove(atomId);
        _owner.Remove(atomId);
    }

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
