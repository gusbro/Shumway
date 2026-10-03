namespace Shumway.Core;

/// <summary>The queue of pending wakes (ADR-049): its array and count live
/// inline in the activation, so the empty test made at every goal boundary and
/// every backtrack is a single field read.</summary>
internal struct WakeQueue
{
    private (int Module, int AttrValueIdx, int OtherIdx, int AttvarHome)[]? _items;

    /// <summary>The number of queued wakes.</summary>
    public int Count;

    public ref (int Module, int AttrValueIdx, int OtherIdx, int AttvarHome) this[int i]
        => ref _items![i];

    public void Add((int Module, int AttrValueIdx, int OtherIdx, int AttvarHome) wake)
    {
        _items ??= new (int, int, int, int)[8];
        if (Count == _items.Length) System.Array.Resize(ref _items, Count * 2);
        _items[Count++] = wake;
    }

    public void Clear() => Count = 0;

    /// <summary>Keeps the first <paramref name="count"/> wakes.</summary>
    public void Truncate(int count)
    {
        if (Count > count) Count = count;
    }
}
