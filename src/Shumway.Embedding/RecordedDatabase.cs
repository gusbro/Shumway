using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>
/// Phase 24 chunk 266 — the Arity-Prolog recorded database. A second
/// in-memory store separate from dynamic predicates, indexed by an
/// arbitrary <em>key term</em> (not the <c>functor/arity</c> a dynamic
/// predicate is). Each <c>recorda/3</c> / <c>recordz/3</c> returns a
/// fresh stable integer <em>reference</em>; <c>erase/1</c> takes a
/// reference and removes precisely that entry; <c>recorded/3</c>
/// enumerates the entries stored under a key on backtracking.
///
/// <para>Per-engine state, populated lazily on first
/// <see cref="PrologEngine.Records"/> access — engines that never
/// touch the recorded DB pay nothing.</para>
///
/// <para>Refs are positive integers from a monotonically-increasing
/// counter. They are never reused, so a ref outlives its entry (an
/// <c>erase</c> followed by a stale <c>instance</c> on the same ref
/// simply fails — it never resurrects another entry).</para>
/// </summary>
public sealed class RecordedDatabase
{
    private int _nextRef = 1;
    private readonly Dictionary<Term, LinkedList<RecordEntry>> _byKey = new();
    private readonly Dictionary<int, RecordEntry> _byRef = new();

    internal RecordedDatabase() { }

    /// <summary>One stored record. Carries its <see cref="LinkedListNode{T}"/>
    /// so <c>erase/1</c> / <c>record_before/3</c> / <c>record_after/3</c>
    /// can splice it out / around in O(1).</summary>
    public sealed class RecordEntry
    {
        public int Ref { get; }
        public Term Key { get; }
        public Term Term { get; internal set; }
        internal LinkedListNode<RecordEntry>? Node;

        internal RecordEntry(int @ref, Term key, Term term)
        {
            Ref = @ref;
            Key = key;
            Term = term;
        }
    }

    /// <summary>Adds <paramref name="term"/> to the start of the chain
    /// of records under <paramref name="key"/>. Returns the fresh
    /// reference.</summary>
    public int Recorda(Term key, Term term) => AddInternal(key, term, atFront: true);

    /// <summary>Adds <paramref name="term"/> to the end of the chain
    /// of records under <paramref name="key"/>. Returns the fresh
    /// reference.</summary>
    public int Recordz(Term key, Term term) => AddInternal(key, term, atFront: false);

    private int AddInternal(Term key, Term term, bool atFront)
    {
        int @ref = _nextRef++;
        if (!_byKey.TryGetValue(key, out var list))
            _byKey[key] = list = new LinkedList<RecordEntry>();
        var entry = new RecordEntry(@ref, key, term);
        entry.Node = atFront ? list.AddFirst(entry) : list.AddLast(entry);
        _byRef[@ref] = entry;
        return @ref;
    }

    /// <summary>Removes the entry with reference <paramref name="ref"/>.
    /// Returns false when no such entry exists (e.g. already erased).</summary>
    public bool Erase(int @ref)
    {
        if (!_byRef.TryGetValue(@ref, out var entry)) return false;
        var list = entry.Node!.List!;
        list.Remove(entry.Node);
        _byRef.Remove(@ref);
        if (list.Count == 0) _byKey.Remove(entry.Key);
        return true;
    }

    /// <summary>Removes every entry under <paramref name="key"/>.</summary>
    public void EraseAll(Term key)
    {
        if (!_byKey.TryGetValue(key, out var list)) return;
        foreach (var e in list) _byRef.Remove(e.Ref);
        _byKey.Remove(key);
    }

    /// <summary>Returns the term stored under <paramref name="ref"/>, or
    /// <c>null</c> when no such entry exists.</summary>
    public Term? Instance(int @ref)
        => _byRef.TryGetValue(@ref, out var e) ? e.Term : null;

    /// <summary>Returns the key the entry with this ref was recorded
    /// under, or <c>null</c> if the ref doesn't exist.</summary>
    public Term? KeyOf(int @ref)
        => _byRef.TryGetValue(@ref, out var e) ? e.Key : null;

    /// <summary>Replaces the term in the entry with reference
    /// <paramref name="ref"/>. Returns false on an unknown ref. The
    /// list position and the ref itself are preserved.</summary>
    public bool Replace(int @ref, Term newTerm)
    {
        if (!_byRef.TryGetValue(@ref, out var entry)) return false;
        entry.Term = newTerm;
        return true;
    }

    /// <summary>Returns the (ref, term) pairs currently stored under
    /// <paramref name="key"/>, in chain order.</summary>
    public IEnumerable<(int Ref, Term Term)> Recorded(Term key)
    {
        if (!_byKey.TryGetValue(key, out var list)) yield break;
        foreach (var e in list) yield return (e.Ref, e.Term);
    }

    /// <summary>Number of entries under <paramref name="key"/>.</summary>
    public int KeyCount(Term key)
        => _byKey.TryGetValue(key, out var list) ? list.Count : 0;

    /// <summary>Snapshot of every key currently in the database.</summary>
    public IEnumerable<Term> AllKeys() => _byKey.Keys.ToArray();

    /// <summary>Whether <paramref name="ref"/> currently refers to a
    /// live entry. Used by <c>ref/1</c>.</summary>
    public bool ContainsRef(int @ref) => _byRef.ContainsKey(@ref);

    /// <summary>Lookup an entry by ref. Used by <c>nref/2</c> / <c>pref/2</c>
    /// for chain traversal.</summary>
    public RecordEntry? GetEntry(int @ref)
        => _byRef.TryGetValue(@ref, out var e) ? e : null;

    /// <summary>The next entry in the same key's chain after the one
    /// with <paramref name="ref"/>. Used by <c>nref/2</c>.</summary>
    public int? NextRef(int @ref)
    {
        if (!_byRef.TryGetValue(@ref, out var e) || e.Node!.Next is null)
            return null;
        return e.Node.Next.Value.Ref;
    }

    /// <summary>The previous entry in the same key's chain. Used by
    /// <c>pref/2</c>.</summary>
    public int? PrevRef(int @ref)
    {
        if (!_byRef.TryGetValue(@ref, out var e) || e.Node!.Previous is null)
            return null;
        return e.Node.Previous.Value.Ref;
    }

    /// <summary>Inserts a term immediately after the entry with
    /// <paramref name="afterRef"/>. Used by <c>record_after/3</c>.
    /// Returns the new ref, or null when <paramref name="afterRef"/>
    /// doesn't exist.</summary>
    public int? RecordAfter(int afterRef, Term term)
    {
        if (!_byRef.TryGetValue(afterRef, out var anchor)) return null;
        int @ref = _nextRef++;
        var entry = new RecordEntry(@ref, anchor.Key, term);
        entry.Node = anchor.Node!.List!.AddAfter(anchor.Node, entry);
        _byRef[@ref] = entry;
        return @ref;
    }

    /// <summary>Inserts a term immediately before
    /// <paramref name="beforeRef"/>. Used by <c>record_before/3</c>.</summary>
    public int? RecordBefore(int beforeRef, Term term)
    {
        if (!_byRef.TryGetValue(beforeRef, out var anchor)) return null;
        int @ref = _nextRef++;
        var entry = new RecordEntry(@ref, anchor.Key, term);
        entry.Node = anchor.Node!.List!.AddBefore(anchor.Node, entry);
        _byRef[@ref] = entry;
        return @ref;
    }

    /// <summary>Removes every entry. Used by tests + by future
    /// snapshot/save-state save_state(... clean_records ...) options.</summary>
    public void Clear()
    {
        _byKey.Clear();
        _byRef.Clear();
        // _nextRef is NOT reset — refs stay forever unique even across
        // clears, matching Arity's behaviour.
    }
}
