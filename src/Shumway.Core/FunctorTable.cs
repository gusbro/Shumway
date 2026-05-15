using System.Collections.Concurrent;

namespace Shumway.Core;

/// <summary>
/// Global, thread-safe intern table mapping <c>(atomId, arity)</c> pairs to a stable
/// integer functor id. Multiple engines share the same instance so functor ids are
/// comparable across engines (see ADR-001 and ADR-008).
/// </summary>
public static class FunctorTable
{
    private static readonly ConcurrentDictionary<(int AtomId, int Arity), int> _byKey = new();
    private static readonly ConcurrentDictionary<int, (int AtomId, int Arity)> _byId = new();
    private static int _nextId;

    /// <summary>
    /// Returns the functor id for <c>(atomId, arity)</c>, allocating a fresh id on first use.
    /// Thread-safe: concurrent callers interning the same pair always converge on a single id.
    /// </summary>
    public static int Intern(int atomId, int arity)
    {
        if (arity < 0)
            throw new ArgumentOutOfRangeException(nameof(arity), arity, "Arity must be non-negative.");

        var key = (atomId, arity);
        if (_byKey.TryGetValue(key, out int existing))
            return existing;

        int candidate = Interlocked.Increment(ref _nextId) - 1;
        _byId[candidate] = key;

        if (_byKey.TryAdd(key, candidate))
            return candidate;

        // Lost the race; drop our orphan and adopt the winner.
        _byId.TryRemove(candidate, out _);
        return _byKey[key];
    }

    /// <summary>Returns <c>(atomId, arity)</c> for a previously-interned functor id.</summary>
    /// <exception cref="ArgumentException">If <paramref name="functorId"/> was never returned by <see cref="Intern"/>.</exception>
    public static (int AtomId, int Arity) Lookup(int functorId)
    {
        if (_byId.TryGetValue(functorId, out var entry))
            return entry;
        throw new ArgumentException($"Unknown functor id {functorId}.", nameof(functorId));
    }

    public static bool TryLookup(int functorId, out (int AtomId, int Arity) entry)
        => _byId.TryGetValue(functorId, out entry);

    /// <summary>Number of distinct functors interned so far. Includes orphaned ids from lost intern races.</summary>
    public static int Count => _byId.Count;

    /// <summary>
    /// Clears the table. Intended only for test isolation; do not call from production code.
    /// </summary>
    internal static void ResetForTesting()
    {
        _byKey.Clear();
        _byId.Clear();
        Interlocked.Exchange(ref _nextId, 0);
    }
}
