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
    private static readonly Dictionary<int, BuiltinEntry> _byId = new();
    private static readonly Dictionary<int, int> _byFunctorId = new();   // functor id → builtin id
    private static int _nextId;

    /// <summary>Registers a builtin under the given name and arity, returning
    /// its id. If a builtin with the same functor is already registered, the
    /// existing id is returned and <paramref name="impl"/> is ignored.
    ///
    /// <para><paramref name="category"/> and <paramref name="summary"/> are
    /// optional user documentation; supply them for user-facing predicates so
    /// the predicate-reference generator picks them up. Leave them null for
    /// internal <c>$</c>-named helpers.</para></summary>
    public static int Register(string name, int arity, BuiltinImpl impl,
        string? category = null, string? summary = null)
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
            _byFunctorId[functorId] = id;
            _byId[id] = new BuiltinEntry(id, name, arity, impl, category, summary);
            return id;
        }
    }

    /// <summary>Look up the builtin id associated with a functor. Returns
    /// false if no builtin is registered under that functor.</summary>
    public static bool TryGetByFunctor(int functorId, out int builtinId)
    {
        lock (_lock)
        {
            return _byFunctorId.TryGetValue(functorId, out builtinId);
        }
    }

    /// <summary>Snapshot of every functor id currently bound to a builtin.
    /// Used by <c>current_predicate/1</c>'s helper to enumerate the
    /// builtin namespace alongside user-defined predicates.</summary>
    public static IReadOnlyCollection<int> AllRegisteredFunctorIds()
    {
        lock (_lock)
        {
            return _byFunctorId.Keys.ToArray();
        }
    }

    /// <summary>Snapshot of every registered builtin entry. Used by the
    /// predicate-reference generator to enumerate the documented builtins.</summary>
    public static IReadOnlyCollection<BuiltinEntry> AllEntries()
    {
        lock (_lock)
        {
            return _byId.Values.ToArray();
        }
    }

    /// <summary>Returns the entry for the given builtin id. The interpreter
    /// uses this on every <c>call_builtin</c> dispatch — the lookup happens
    /// once per call, no allocation.</summary>
    public static BuiltinEntry GetById(int builtinId)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(builtinId, out var entry))
                throw new InvalidOperationException(
                    $"No builtin registered with id {builtinId}.");
            return entry;
        }
    }
}
