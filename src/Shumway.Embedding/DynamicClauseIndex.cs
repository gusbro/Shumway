using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>A first-argument index over a dynamic predicate's PHYSICAL clause
/// list, so retract/1 can jump to the clauses that could possibly match
/// instead of trying every one of them.
///
/// <para>It answers a NECESSARY condition, never a verdict: a clause's key is
/// derived from its head's first argument, which never changes once the clause
/// is asserted, and any shape that cannot PROVE a mismatch keys
/// <see cref="DynFirstArgKey.Anything"/> and therefore stays a candidate for
/// every call. The caller still runs the real unification, and still decides
/// visibility. That is what makes this sound under the logical update view:
/// born/died move over time, the key does not, and a view can only ever make
/// FEWER clauses visible than the physical list holds.</para>
///
/// <para>Positions shift on every insert and removal, so the index is not keyed
/// by position: each clause gets a sequence number, ascending in clause order
/// (assertz takes the next one up, asserta the next one down), and the buckets
/// hold sequence numbers. Nothing is ever renumbered, and a position is one
/// binary search away in either direction.</para></summary>
internal sealed class DynamicClauseIndex
{
    /// <summary>Sequence numbers, parallel to the clause list and ascending —
    /// so position order IS sequence order, and a binary search converts
    /// between them.</summary>
    private readonly List<long> _seqs = new();

    private readonly Dictionary<DynFirstArgKey, List<long>> _byKey = new();

    /// <summary>Clauses whose first argument rules nothing out (a variable, a
    /// float, a big integer). They are a candidate for every call, so every
    /// lookup merges them in.</summary>
    private readonly List<long> _anything = new();

    private long _nextUp, _nextDown = -1;

    public int Count => _seqs.Count;

    public void Append(Clause c)
    {
        long seq = _nextUp++;
        _seqs.Add(seq);
        Bucket(DynFirstArgKey.Of(c)).Add(seq);
    }

    public void Prepend(Clause c)
    {
        long seq = _nextDown--;
        _seqs.Insert(0, seq);
        Bucket(DynFirstArgKey.Of(c)).Insert(0, seq);
    }

    /// <summary>The clause at <paramref name="index"/> has been tombstoned.
    /// Its sequence number STAYS in <see cref="_seqs"/> because the store
    /// keeps the slot and no position moves; only the bucket entry goes,
    /// which is what stops the clause being a candidate.
    ///
    /// <para><paramref name="c"/> is the clause that was there, passed in so
    /// its bucket is found by its own key — searching every bucket for the
    /// sequence number would be O(distinct keys) per retract, the very cost
    /// this index exists to remove.</para></summary>
    public void MarkDead(int index, Clause c)
        => RemoveSeq(Bucket(DynFirstArgKey.Of(c)), _seqs[index]);

    /// <summary>Rebuilds from the clause list. Used when the list changes in a
    /// way the incremental path does not describe (bulk load, in-place clause
    /// replacement, a slot swapped out), and as the self-heal when the index
    /// and the list disagree on how many clauses there are.</summary>
    public void Rebuild(List<Clause> clauses)
    {
        _seqs.Clear();
        _byKey.Clear();
        _anything.Clear();
        _nextUp = 0;
        _nextDown = -1;
        foreach (var c in clauses) Append(c);
    }

    /// <summary>The position of the first clause at or after
    /// <paramref name="fromIndex"/> whose key does not rule it out for a call
    /// keyed <paramref name="key"/>, or -1 when no such clause exists.</summary>
    public int FirstCandidateFrom(DynFirstArgKey key, int fromIndex)
    {
        if (fromIndex >= _seqs.Count) return -1;
        long fromSeq = _seqs[fromIndex];
        long best = long.MaxValue;
        if (_byKey.TryGetValue(key, out var keyed)
            && FirstAtOrAfter(keyed, fromSeq) is { } a) best = a;
        if (FirstAtOrAfter(_anything, fromSeq) is { } b && b < best) best = b;
        if (best == long.MaxValue) return -1;
        int pos = _seqs.BinarySearch(best);
        return pos < 0 ? -1 : pos;
    }

    private List<long> Bucket(DynFirstArgKey key)
    {
        if (key.MatchesEverything) return _anything;
        if (!_byKey.TryGetValue(key, out var bucket))
            _byKey[key] = bucket = new List<long>();
        return bucket;
    }

    private static bool RemoveSeq(List<long> bucket, long seq)
    {
        int i = bucket.BinarySearch(seq);
        if (i < 0) return false;
        bucket.RemoveAt(i);
        return true;
    }

    private static long? FirstAtOrAfter(List<long> sorted, long seq)
    {
        int i = sorted.BinarySearch(seq);
        if (i < 0) i = ~i;
        return i < sorted.Count ? sorted[i] : null;
    }
}
