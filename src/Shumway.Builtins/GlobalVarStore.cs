using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Per-engine name → cell store backing SWI's <c>nb_setval</c> /
/// <c>nb_getval</c> family (chunk 145). Two kinds of binding:
///
/// <list type="bullet">
/// <item><b>Non-backtrackable</b> (<c>nb_setval/2</c>): the write
///   persists across backtracking. The store just records the
///   latest cell value.</item>
/// <item><b>Backtrackable</b> (<c>b_setval/2</c>): the write is
///   reverted on backtrack — Phase 9.5 stub records it
///   non-backtrackably until the ExtraTrail integration lands.
///   (Most programs use <c>nb_setval/2</c>; documented inline.)</item>
/// </list>
///
/// <para>Cells stored here are snapshots taken at write time —
/// see <c>Engine.SnapshotIntoHeap</c>. The store survives across
/// queries on the hosting engine.</para>
/// </summary>
public sealed class GlobalVarStore
{
    private readonly Dictionary<string, Cell> _byName = new();

    public void Set(string name, Cell value, bool backtrackable)
    {
        // Backtrackable storage is treated as non-backtrackable in
        // this first cut — see the class remarks. A future chunk
        // can plumb the previous value through the engine's
        // ExtraTrail so the binding reverts on backtrack.
        _ = backtrackable;
        _byName[name] = value;
    }

    public bool TryGet(string name, out Cell value) =>
        _byName.TryGetValue(name, out value);

    public IEnumerable<(string Name, Cell Value)> All() =>
        _byName.Select(p => (p.Key, p.Value));

    /// <summary>ADR-016 — rewrites every stored cell through the heap
    /// collector's relocation map. A no-op for value-bearing cells
    /// (Int/Atom/Float/BigInt/Foreign carry no heap index); cells that
    /// reference the heap (Str/Lis/Pstr) get their payload remapped so
    /// they survive a mid-query compaction.</summary>
    public void RelocateCells(System.Func<Cell, Cell> reloc)
    {
        foreach (var name in _byName.Keys.ToList())
            _byName[name] = reloc(_byName[name]);
    }
}
