namespace Shumway.Core;

/// <summary>Read-only byte arrays held once per content: the engines of a
/// process that load the same blob (a bundle's wasm module) hold one array.
/// An array is forgotten when nothing else holds it. What comes back must
/// not be written to.</summary>
public sealed class SharedByteArrays
{
    private const int SweepEvery = 256;

    private readonly object _gate = new();
    // By length: equal contents have equal lengths, and few blobs share one.
    private readonly Dictionary<int, List<WeakReference<byte[]>>> _byLength = new();
    private int _addedSinceSweep;

    /// <summary>The array already held with the contents of
    /// <paramref name="bytes"/>; failing one, the array <paramref name="keep"/>
    /// makes of them (<paramref name="bytes"/> itself when null), held from
    /// now on.</summary>
    public byte[] Share(byte[] bytes, Func<byte[], byte[]>? keep = null)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        lock (_gate)
        {
            if (!_byLength.TryGetValue(bytes.Length, out var same))
                _byLength[bytes.Length] = same = new List<WeakReference<byte[]>>(1);
            for (int i = same.Count - 1; i >= 0; i--)
            {
                if (!same[i].TryGetTarget(out byte[]? held)) { same.RemoveAt(i); continue; }
                if (held.AsSpan().SequenceEqual(bytes)) return held;
            }
            byte[] kept = keep is null ? bytes : keep(bytes);
            same.Add(new WeakReference<byte[]>(kept));
            if (++_addedSinceSweep >= SweepEvery) Sweep();
            return kept;
        }
    }

    /// <summary>How many arrays are held. For tests.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                Sweep();
                int n = 0;
                foreach (var same in _byLength.Values) n += same.Count;
                return n;
            }
        }
    }

    // A program that compiles for hours shares thousands of arrays nobody
    // holds any more: their entries go here, not at the next equal length.
    private void Sweep()
    {
        _addedSinceSweep = 0;
        List<int>? empty = null;
        foreach (var entry in _byLength)
        {
            entry.Value.RemoveAll(static w => !w.TryGetTarget(out _));
            if (entry.Value.Count == 0) (empty ??= new List<int>()).Add(entry.Key);
        }
        if (empty is not null)
            foreach (int length in empty) _byLength.Remove(length);
    }
}
