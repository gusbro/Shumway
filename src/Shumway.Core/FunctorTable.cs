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

    // Chunk 428 — dense lock-free by-id fast path for Lookup/TryLookup.
    // Lookup is paid on every compound-compound unification (UnifyStr,
    // OccursIn, StructuralCompareIterative) and per live Str cell in the heap
    // GC mark, so the ConcurrentDictionary probe was hot-path cost. Functor
    // ids are dense (Interlocked.Increment above), so a flat array indexed
    // by id — grown copy-on-write, the AtomTable._permanentByIdArray
    // precedent — serves the common case with one bounds check + one read.
    // Each entry packs (atomId << 32 | arity) into a single long so a slot
    // is written/read atomically (Volatile.Write/Read on a long is atomic
    // on every runtime); -1 marks "not published yet" (valid entries are
    // always >= 0 since atom ids and arities are non-negative). Writes go
    // through _byIdArrayLock (rare: once per interned functor + the
    // republish-on-miss path); racing readers see either the old array or
    // the new one, and either -1 (fall back to the dictionary) or the
    // final value — entries are immutable once written.
    private const long EmptyByIdEntry = -1L;
    private static volatile long[] _byIdArray = CreateByIdArray(1024);
    private static readonly object _byIdArrayLock = new();

    private static long[] CreateByIdArray(int size)
    {
        var arr = new long[size];
        Array.Fill(arr, EmptyByIdEntry);
        return arr;
    }

    /// <summary>Publishes <c>(atomId, arity)</c> into the dense by-id array,
    /// growing it copy-on-write when <paramref name="id"/> is out of range.</summary>
    private static void StoreInByIdArray(int id, int atomId, int arity)
    {
        lock (_byIdArrayLock)
        {
            long[] arr = _byIdArray;
            if (id >= arr.Length)
            {
                int newSize = arr.Length * 2;
                while (newSize <= id) newSize *= 2;
                var newArr = CreateByIdArray(newSize);
                Array.Copy(arr, newArr, arr.Length);
                arr = newArr;
            }
            Volatile.Write(ref arr[id], ((long)atomId << 32) | (uint)arity);
            _byIdArray = arr;
        }
    }

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
        {
            // Publish to the dense array only for the canonical (winning)
            // id — a lost-race orphan must never appear there, since the
            // dictionary entry it mirrors is removed below.
            StoreInByIdArray(candidate, atomId, arity);
            return candidate;
        }

        // Lost the race; drop our orphan and adopt the winner.
        _byId.TryRemove(candidate, out _);
        return _byKey[key];
    }

    /// <summary>Returns <c>(atomId, arity)</c> for a previously-interned functor id.</summary>
    /// <exception cref="ArgumentException">If <paramref name="functorId"/> was never returned by <see cref="Intern"/>.</exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static (int AtomId, int Arity) Lookup(int functorId)
    {
        // Chunk 428 — dense-array fast path; on a 64-bit runtime the
        // volatile long read is a plain mov.
        long[] arr = _byIdArray;
        if ((uint)functorId < (uint)arr.Length)
        {
            long packed = Volatile.Read(ref arr[functorId]);
            if (packed >= 0) return ((int)((ulong)packed >> 32), (int)packed);
        }
        return LookupSlow(functorId);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (int AtomId, int Arity) LookupSlow(int functorId)
    {
        if (_byId.TryGetValue(functorId, out var entry))
        {
            // Republish so the next lookup of this id hits the array
            // (covers ids interned before a ResetForTesting-recreated
            // array, or seen by this path before the intern published).
            StoreInByIdArray(functorId, entry.AtomId, entry.Arity);
            return entry;
        }
        throw new ArgumentException($"Unknown functor id {functorId}.", nameof(functorId));
    }

    public static bool TryLookup(int functorId, out (int AtomId, int Arity) entry)
    {
        long[] arr = _byIdArray;
        if ((uint)functorId < (uint)arr.Length)
        {
            long packed = Volatile.Read(ref arr[functorId]);
            if (packed >= 0)
            {
                entry = ((int)((ulong)packed >> 32), (int)packed);
                return true;
            }
        }
        if (_byId.TryGetValue(functorId, out entry))
        {
            StoreInByIdArray(functorId, entry.AtomId, entry.Arity);
            return true;
        }
        return false;
    }

    /// <summary>Number of distinct functors interned so far. Includes orphaned ids from lost intern races.</summary>
    public static int Count => _byId.Count;

    /// <summary>
    /// Clears the table. Intended only for test isolation; do not call from production code.
    /// </summary>
    internal static void ResetForTesting()
    {
        lock (_byIdArrayLock)
        {
            _byKey.Clear();
            _byId.Clear();
            Interlocked.Exchange(ref _nextId, 0);
            _byIdArray = CreateByIdArray(1024);
        }
    }
}
