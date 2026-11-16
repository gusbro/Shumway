namespace Shumway.Embedding;

/// <summary>A map that holds at most a fixed number of entries: adding one
/// past that drops the oldest, so an entry just added outlives the next
/// capacity - 1 additions whatever else adds. Not thread-safe; the caller
/// locks.</summary>
internal sealed class OldestOutCache<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, TValue> _entries = new();
    private readonly Queue<TKey> _order = new();

    public OldestOutCache(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _entries.Count;

    /// <summary>The value held for <paramref name="key"/>, or null.</summary>
    public TValue? Find(TKey key) => _entries.TryGetValue(key, out var value) ? value : null;

    /// <summary>Holds a key that is not held yet.</summary>
    public void Add(TKey key, TValue value)
    {
        if (_entries.Count >= _capacity) _entries.Remove(_order.Dequeue());
        _entries.Add(key, value);
        _order.Enqueue(key);
    }
}
