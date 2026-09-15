using System.Collections.Generic;

namespace Shumway.Core;

/// <summary>The attributed-variable store's ONE set of writers.
///
/// <para>The store maps an attributed variable's heap home index to its
/// record, itself a map from a module's atom id to the heap index of that
/// module's attribute value. It used to be written from six places across
/// three files, four of them through a reference to the inner record handed
/// out by the table -- so a seventh writer could be added without anything
/// noticing, and any DERIVED view of the store would silently fall out of
/// step. That is the exact shape of a bug this engine has already paid for
/// once (a seeded clause that bypassed the dynamic-store mutation funnel and
/// cost a library its hook, silently and depending on load order).</para>
///
/// <para>So the store is private to this file's methods, and the inner
/// record is never returned. Every mutation lands in one of five places:
/// <see cref="AttrCreateRecord"/>, <see cref="AttrSet"/>,
/// <see cref="AttrRemove"/>, <see cref="AttrDropRecord"/> and
/// <see cref="AttrRekeyAll"/>. A derived view -- the heap GC's scan today, a
/// linear-memory mirror the wasm tier can read tomorrow -- is maintained
/// from those five and nowhere else.</para>
///
/// <para>Trailing is NOT here. A mutation's undo record belongs to the
/// caller that knows the semantics (PutAttr promotes and trails a
/// ValueChange; DelAttr may demote), and burying it here would make the
/// funnel look like it guarantees more than it does.</para></summary>
public sealed partial class Activation
{
    // ----- writers -----

    /// <summary>Installs a fresh, empty record for <paramref name="home"/>,
    /// overwriting any orphan a backtracked-then-reused slot left behind.</summary>
    private void AttrCreateRecord(int home) => _attrStore[home] = new Dictionary<int, int>();

    /// <summary>Sets <paramref name="moduleId"/>'s attribute value. The
    /// record must exist.</summary>
    private void AttrSet(int home, int moduleId, int valueHeapIdx)
        => _attrStore[home][moduleId] = valueHeapIdx;

    /// <summary>Removes <paramref name="moduleId"/>'s attribute; returns the
    /// number of attributes left on that variable, or -1 when it had no
    /// record at all. The count is what tells a caller to demote the cell
    /// back to a plain variable.</summary>
    private int AttrRemove(int home, int moduleId)
    {
        if (!_attrStore.TryGetValue(home, out var record)) return -1;
        record.Remove(moduleId);
        return record.Count;
    }

    /// <summary>Drops the whole record: the cell at <paramref name="home"/>
    /// is no longer a live attributed variable.</summary>
    private void AttrDropRecord(int home) => _attrStore.Remove(home);

    /// <summary>Re-keys the whole store after the heap collector moved
    /// cells: both the homes (the keys) and the attribute values (the
    /// payloads) are heap indices. One pass, one place.</summary>
    private void AttrRekeyAll(System.Func<int, int> reloc)
    {
        if (_attrStore.Count == 0) return;
        var moved = new List<(int Home, Dictionary<int, int> Record)>(_attrStore.Count);
        foreach (var kv in _attrStore)
        {
            var record = kv.Value;
            foreach (int module in new List<int>(record.Keys))
                record[module] = reloc(record[module]);
            moved.Add((reloc(kv.Key), record));
        }
        _attrStore.Clear();
        foreach (var (home, record) in moved) _attrStore[home] = record;
    }

    // ----- readers -----

    /// <summary>The attribute value's heap index, or -1 when absent. Does
    /// not deref and does not check the cell's tag: the callers that care do
    /// both first.</summary>
    private int AttrValueAt(int home, int moduleId)
        => _attrStore.TryGetValue(home, out var record)
            && record.TryGetValue(moduleId, out int value) ? value : -1;

    private bool AttrHasRecord(int home) => _attrStore.ContainsKey(home);

    /// <summary>The module ids carrying an attribute on <paramref
    /// name="home"/>. A snapshot: the caller may mutate the store while
    /// iterating, which the live key collection would not survive.</summary>
    private int[] AttrModulesAt(int home)
        => _attrStore.TryGetValue(home, out var record)
            ? System.Linq.Enumerable.ToArray(record.Keys) : System.Array.Empty<int>();

    /// <summary>Queues a wakeup for every module attributed to
    /// <paramref name="home"/>, against <paramref name="otherIdx"/>.
    ///
    /// <para>An OPERATION rather than a getter, and deliberately: this runs
    /// on every binding of an attributed variable -- six call sites in the
    /// unify ops -- so handing out a snapshot would put an allocation on the
    /// hottest path attributes have. Iterating in place costs nothing, and
    /// the record still never escapes the funnel. Nothing here can mutate
    /// the store, so iterating it live is safe.</para></summary>
    private void AttrQueueWakeups(int home, int otherIdx,
        List<(int Module, int AttrValueIdx, int OtherIdx, int AttvarHome)> into)
    {
        if (!_attrStore.TryGetValue(home, out var record)) return;
        foreach (var (moduleId, attrValueIdx) in record)
            into.Add((moduleId, attrValueIdx, otherIdx, home));
    }

    /// <summary>The (module, value) pairs on <paramref name="home"/>, as a
    /// snapshot for the same reason.</summary>
    private (int Module, int Value)[] AttrPairsAt(int home)
    {
        if (!_attrStore.TryGetValue(home, out var record))
            return System.Array.Empty<(int, int)>();
        var pairs = new (int, int)[record.Count];
        int i = 0;
        foreach (var (m, v) in record) pairs[i++] = (m, v);
        return pairs;
    }

    /// <summary>Every (home, module, value) the store holds. For the whole-
    /// store passes: the GC's root marking and the debug sweeps.</summary>
    private IEnumerable<(int Home, int Module, int Value)> AttrAll()
    {
        foreach (var kv in _attrStore)
            foreach (var (module, value) in kv.Value)
                yield return (kv.Key, module, value);
    }

    /// <summary>The homes the store holds, as a snapshot.</summary>
    private int[] AttrHomes()
    {
        var keys = new int[_attrStore.Count];
        _attrStore.Keys.CopyTo(keys, 0);
        return keys;
    }

    private int AttrRecordTotal => _attrStore.Count;
}
