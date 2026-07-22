using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Process-wide registry of builtin predicates. Each registered builtin gets a
/// stable integer id that the WAM compiler bakes into <c>call_builtin</c>
/// operands and the interpreter uses to dispatch the implementation. The
/// registry is keyed by functor (<see cref="AtomTable.Intern"/> +
/// <see cref="FunctorTable.Intern"/>), so the compiler's "is this name/arity a
/// builtin?" lookup is a single dictionary probe.
///
/// <para>Registration is idempotent: a second <see cref="Register"/> of the
/// same name/arity returns the existing id rather than creating a duplicate.
/// This lets <see cref="StandardBuiltins.EnsureRegistered"/> be safely called
/// from multiple entry points (test setup, embedding-API constructor) without
/// worrying about ordering.</para>
/// </summary>
public static class BuiltinsRegistry
{
    private static readonly object _lock = new();
    // GetById sits on the call_builtin hot path, so dispatch reads
    // `_entries[id]` lock-free. The array is grown copy-on-write under
    // `_lock` so concurrent reads see either the old reference or the new
    // one — never a torn modification. IDs are contiguous (Register hands
    // them out via `_nextId++`) so direct indexing always lands on a
    // populated slot.
    private static volatile BuiltinEntry?[] _entries = System.Array.Empty<BuiltinEntry?>();
    // functor id -> builtin id. Same lock-free read pattern as
    // _entries: writes happen under `_lock`, but readers
    // (TryGetByFunctor on the call_builtin dispatch path) read a
    // volatile reference whose contents are never mutated in place —
    // each modification snapshots, mutates the copy, and atomically
    // swaps the field. ConcurrentDictionary would do the same with
    // more overhead.
    private static volatile Dictionary<int, int> _byFunctorId = new();
    private static int _nextId;

    /// <summary>Registers a builtin under the given name and arity, returning
    /// its id. If a builtin with the same functor is already registered, the
    /// existing id is returned and <paramref name="impl"/> is ignored.
    ///
    /// <para><paramref name="category"/>, <paramref name="template"/> and
    /// <paramref name="summary"/> are optional user documentation; supply them
    /// for user-facing predicates so the predicate-reference generator picks
    /// them up. <paramref name="template"/> is the moded call template, e.g.
    /// <c>between(+Low, +High, ?X)</c>. Leave them null for internal
    /// <c>$</c>-named helpers.</para></summary>
    public static int Register(string name, int arity, BuiltinImpl impl,
        string? category = null, string? template = null, string? summary = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(impl);
        if (arity < 0) throw new ArgumentOutOfRangeException(nameof(arity));

        int functorId = FunctorTable.Intern(
            AtomTable.Intern(name, permanent: true).Id, arity);

        lock (_lock)
        {
            if (_byFunctorId.TryGetValue(functorId, out int existing))
                return existing;
            int id = _nextId++;
            // Snapshot-then-swap so concurrent readers (the dispatcher
            // hot path) never observe a partial write.
            var newMap = new Dictionary<int, int>(_byFunctorId);
            newMap[functorId] = id;
            _byFunctorId = newMap;
            // Grow the array if needed. Copy-on-write so any
            // concurrent reader either keeps using the old reference or
            // picks up the new one on its next dereference — neither sees
            // a half-written slot.
            BuiltinEntry?[] arr = _entries;
            if (id >= arr.Length)
            {
                int newSize = arr.Length == 0 ? 32 : arr.Length * 2;
                while (newSize <= id) newSize *= 2;
                var newArr = new BuiltinEntry?[newSize];
                System.Array.Copy(arr, newArr, arr.Length);
                arr = newArr;
            }
            arr[id] = new BuiltinEntry(
                id, name, arity, impl, category, template, summary);
            _entries = arr;
            return id;
        }
    }

    /// <summary>Look up the builtin id associated with a functor. Returns
    /// false if no builtin is registered under that functor.</summary>
    public static bool TryGetByFunctor(int functorId, out int builtinId)
    {
        // Lock-free read against the snapshot-published dictionary.
        // Writes go through Register's lock + copy-on-write swap, so
        // concurrent readers see either the old map or the new map —
        // never a torn modification.
        return _byFunctorId.TryGetValue(functorId, out builtinId);
    }

    /// <summary>Snapshot of every functor id currently bound to a builtin.
    /// Used by <c>current_predicate/1</c>'s helper to enumerate the
    /// builtin namespace alongside user-defined predicates.</summary>
    public static IReadOnlyCollection<int> AllRegisteredFunctorIds()
    {
        return _byFunctorId.Keys.ToArray();
    }

    /// <summary>Snapshot of every registered builtin entry. Used by the
    /// predicate-reference generator to enumerate the documented builtins.</summary>
    public static IReadOnlyCollection<BuiltinEntry> AllEntries()
    {
        lock (_lock)
        {
            BuiltinEntry?[] arr = _entries;
            var result = new List<BuiltinEntry>(arr.Length);
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] is { } e) result.Add(e);
            return result;
        }
    }

    /// <summary>Returns the entry for the given builtin id. The interpreter
    /// uses this on every <c>call_builtin</c> dispatch — the lookup happens
    /// once per call, no allocation, no lock.</summary>
    public static BuiltinEntry GetById(int builtinId)
    {
        BuiltinEntry?[] arr = _entries;
        if ((uint)builtinId >= (uint)arr.Length || arr[builtinId] is not { } entry)
            throw new InvalidOperationException(
                $"No builtin registered with id {builtinId}.");
        return entry;
    }
}
