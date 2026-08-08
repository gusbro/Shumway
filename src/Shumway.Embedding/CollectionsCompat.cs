namespace Shumway.Embedding;

/// <summary>One shim for one gap: net48's <c>Dictionary</c> has no
/// copy-constructor from <c>IReadOnlyDictionary</c> (only from
/// <c>IDictionary</c>), and constructors cannot be supplied by extension.
/// Query setup copies address maps on this path, so the modern target keeps
/// the BCL constructor — which pre-sizes and copies without per-entry
/// delegate calls.</summary>
internal static class CollectionsCompat
{
    public static Dictionary<TKey, TValue> Copy<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> source) where TKey : notnull
    {
#if NETFRAMEWORK
        var copy = new Dictionary<TKey, TValue>(source.Count);
        foreach (var (key, value) in source) copy[key] = value;
        return copy;
#else
        return new Dictionary<TKey, TValue>(source);
#endif
    }
}
