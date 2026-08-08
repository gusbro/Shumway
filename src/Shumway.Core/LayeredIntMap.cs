using System.Collections;

namespace Shumway.Core;

/// <summary>
/// Two-layer int-keyed read-mostly map: a small mutable per-query
/// <em>overlay</em> over a shared frozen <em>base</em>. Query setup used to
/// COPY the persistent base dictionaries (functor→address, address→predicate)
/// into fresh per-query dictionaries — an O(program) copy per query that
/// dominated warm setup on large programs. The overlay holds the per-query
/// entries (query-region links, bare-name aliases, mid-query trampolines) and
/// wins on lookup; the base is the persistent cache, shared BY REFERENCE.
///
/// <para>Freeze contract: the base must never be mutated in place while a view
/// over it is alive. The persistent caches honor this — a persistent rebuild
/// swaps the cache fields to fresh dictionaries, so a suspended activation's
/// view keeps the old frozen base, exactly like the old copy did.</para>
///
/// <para>Writes go to the overlay via the indexer setter (the mid-query
/// trampoline / helper-link paths pattern-match this concrete type — they used
/// to cast <c>CurrentFunctorAddresses</c> back to <c>Dictionary</c>).</para>
/// </summary>
public sealed class LayeredIntMap<TValue> : IReadOnlyDictionary<int, TValue>
{
    private readonly Dictionary<int, TValue> _overlay;
    private readonly IReadOnlyDictionary<int, TValue> _base;
    private int _shadowed;   // overlay keys that also exist in the base

    public LayeredIntMap(Dictionary<int, TValue> overlay, IReadOnlyDictionary<int, TValue> baseMap)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(baseMap);
        _overlay = overlay;
        _base = baseMap;
        foreach (var kv in overlay)
            if (baseMap.ContainsKey(kv.Key)) _shadowed++;
    }

    public TValue this[int key]
    {
        get => _overlay.TryGetValue(key, out var v) ? v : _base[key];
        set
        {
            if (!_overlay.ContainsKey(key) && _base.ContainsKey(key)) _shadowed++;
            _overlay[key] = value;
        }
    }

    public int Count => _overlay.Count + _base.Count - _shadowed;

    public bool ContainsKey(int key)
        => _overlay.ContainsKey(key) || _base.ContainsKey(key);

    public bool TryGetValue(int key, out TValue value)
        => _overlay.TryGetValue(key, out value!) || _base.TryGetValue(key, out value!);

    public IEnumerable<int> Keys
    {
        get
        {
            foreach (var kv in _overlay) yield return kv.Key;
            foreach (var kv in _base)
                if (!_overlay.ContainsKey(kv.Key)) yield return kv.Key;
        }
    }

    public IEnumerable<TValue> Values
    {
        get
        {
            foreach (var kv in _overlay) yield return kv.Value;
            foreach (var kv in _base)
                if (!_overlay.ContainsKey(kv.Key)) yield return kv.Value;
        }
    }

    public IEnumerator<KeyValuePair<int, TValue>> GetEnumerator()
    {
        foreach (var kv in _overlay) yield return kv;
        foreach (var kv in _base)
            if (!_overlay.ContainsKey(kv.Key)) yield return kv;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
