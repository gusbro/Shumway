namespace Shumway.Compiler.Wam;

/// <summary>
/// Append-only, deduplicating list of literal values keyed by their content.
/// Used by the WAM compiler to assign stable integer ids to the string and
/// float literals it encounters across a module's clauses; the interpreter
/// then resolves those ids against the pool at run time.
/// </summary>
public sealed class LiteralPool<T> where T : notnull
{
    private readonly Dictionary<T, int> _byContent = new();
    private readonly List<T> _byId = new();

    public int Intern(T value)
    {
        if (_byContent.TryGetValue(value, out int id)) return id;
        id = _byId.Count;
        _byId.Add(value);
        _byContent[value] = id;
        return id;
    }

    public IReadOnlyList<T> Snapshot() => _byId.ToArray();
    /// <summary>A live, copy-free view of the interned values (the pool only
    /// grows by append, so existing indices stay stable).</summary>
    public IReadOnlyList<T> Items => _byId;
    public int Count => _byId.Count;
}
