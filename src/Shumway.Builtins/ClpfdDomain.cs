using System;

namespace Shumway.Builtins;

/// <summary>
/// An immutable CLP(FD) finite domain: a sorted list of disjoint, non-adjacent
/// closed intervals <c>[lo, hi]</c> over the integers, with <c>long.MinValue</c>
/// standing for −∞ (<c>inf</c>) and <c>long.MaxValue</c> for +∞ (<c>sup</c>).
///
/// <para>A domain is a single C# object (stored in the engine's foreign-object
/// table, referenced by a <c>Foreign</c> cell from the <c>fd(Dom, Props)</c>
/// attribute), so every operation is native — interval walking dominates
/// finite-domain solving, and a Prolog-heap list representation was far
/// slower. Domains are immutable, so backtracking — which restores the trailed
/// attribute and thus the old domain reference — needs no per-domain trailing;
/// the foreign table simply grows.</para>
/// </summary>
public sealed class ClpfdDomain
{
    public const long Inf = long.MinValue;
    public const long Sup = long.MaxValue;

    // Flattened [lo0, hi0, lo1, hi1, ...]; sorted, disjoint, with a real gap
    // between consecutive intervals (hi_i + 1 < lo_{i+1} for finite hi_i).
    private readonly long[] _iv;

    private ClpfdDomain(long[] iv) => _iv = iv;

    public static readonly ClpfdDomain Empty = new(Array.Empty<long>());
    public static readonly ClpfdDomain Universal = new(new[] { Inf, Sup });

    public bool IsEmpty => _iv.Length == 0;

    /// <summary>A single interval [lo, hi] (lo ≤ hi), or Empty if lo &gt; hi.</summary>
    public static ClpfdDomain Interval(long lo, long hi) =>
        lo > hi ? Empty : new ClpfdDomain(new[] { lo, hi });

    public long Min => _iv.Length == 0 ? throw new InvalidOperationException("empty domain") : _iv[0];
    public long Max => _iv.Length == 0 ? throw new InvalidOperationException("empty domain") : _iv[_iv.Length - 1];

    /// <summary>The single value if this is a singleton domain, else has no value
    /// (returns false).</summary>
    public bool TrySingleton(out long v)
    {
        if (_iv.Length == 2 && _iv[0] == _iv[1]) { v = _iv[0]; return true; }
        v = 0;
        return false;
    }

    /// <summary>Number of values, or <paramref name="infinite"/> when an endpoint
    /// is unbounded (the caller supplies the sentinel the old Prolog used).</summary>
    public long Size(long infinite)
    {
        long n = 0;
        for (int i = 0; i < _iv.Length; i += 2)
        {
            long lo = _iv[i], hi = _iv[i + 1];
            if (lo == Inf || hi == Sup) return infinite;
            n += hi - lo + 1;
        }
        return n;
    }

    public bool Contains(long v)
    {
        for (int i = 0; i < _iv.Length; i += 2)
            if (v >= _iv[i] && v <= _iv[i + 1]) return true;
        return false;
    }

    public bool SameAs(ClpfdDomain other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (_iv.Length != other._iv.Length) return false;
        for (int i = 0; i < _iv.Length; i++)
            if (_iv[i] != other._iv[i]) return false;
        return true;
    }

    /// <summary>Keep only the part at or below the bound B.</summary>
    public ClpfdDomain Above(long b)
    {
        var w = new long[_iv.Length];
        int n = 0;
        for (int i = 0; i < _iv.Length; i += 2)
        {
            long lo = _iv[i], hi = _iv[i + 1];
            if (lo > b) break;                 // sorted — nothing later qualifies
            w[n++] = lo;
            w[n++] = (hi <= b) ? hi : b;
            if (hi > b) break;
        }
        return Make(w, n);
    }

    /// <summary>Keep only the part at or above the bound B.</summary>
    public ClpfdDomain Below(long b)
    {
        var w = new long[_iv.Length];
        int n = 0;
        for (int i = 0; i < _iv.Length; i += 2)
        {
            long lo = _iv[i], hi = _iv[i + 1];
            if (hi < b) continue;              // wholly below the bound
            w[n++] = (lo >= b) ? lo : b;
            w[n++] = hi;
        }
        return Make(w, n);
    }

    /// <summary>Remove the single value V (splitting an interval if interior).</summary>
    public ClpfdDomain Without(long v)
    {
        if (!Contains(v)) return this;
        var w = new long[_iv.Length + 2];      // at most one extra interval
        int n = 0;
        for (int i = 0; i < _iv.Length; i += 2)
        {
            long lo = _iv[i], hi = _iv[i + 1];
            if (v < lo || v > hi) { w[n++] = lo; w[n++] = hi; continue; }
            if (lo < v) { w[n++] = lo; w[n++] = v - 1; }   // safe: lo<v so v-1≥lo, no underflow at inf
            if (v < hi) { w[n++] = v + 1; w[n++] = hi; }   // safe: v<hi so v+1≤hi, no overflow at sup
        }
        return Make(w, n);
    }

    /// <summary>Union of two domains, merging overlapping/adjacent intervals.</summary>
    public ClpfdDomain Union(ClpfdDomain other)
    {
        long[] a = _iv, b = other._iv;
        var w = new long[a.Length + b.Length];
        int n = 0, i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            long lo, hi;
            bool takeA = j >= b.Length || (i < a.Length && a[i] <= b[j]);
            if (takeA) { lo = a[i]; hi = a[i + 1]; i += 2; }
            else { lo = b[j]; hi = b[j + 1]; j += 2; }
            // Merge into the last emitted interval if it touches (adjacent or
            // overlapping). hi+1 guard avoids overflow at sup.
            if (n > 0 && (lo == Inf || w[n - 1] >= lo - 1))
            {
                if (hi > w[n - 1]) w[n - 1] = hi;
            }
            else { w[n++] = lo; w[n++] = hi; }
        }
        return Make(w, n);
    }

    /// <summary>Intersection of two domains (both sorted-disjoint).</summary>
    public ClpfdDomain Intersect(ClpfdDomain other)
    {
        var w = new long[_iv.Length + other._iv.Length];
        int n = 0, i = 0, j = 0;
        long[] a = _iv, b = other._iv;
        while (i < a.Length && j < b.Length)
        {
            long lo = Math.Max(a[i], b[j]);
            long hi = Math.Min(a[i + 1], b[j + 1]);
            if (lo <= hi) { w[n++] = lo; w[n++] = hi; }
            // advance whichever interval ends first
            if (a[i + 1] < b[j + 1]) i += 2; else j += 2;
        }
        return Make(w, n);
    }

    /// <summary>This domain with the finite integer interval [lo, hi] removed.</summary>
    public ClpfdDomain RemoveInterval(long lo, long hi) => Above(lo - 1).Union(Below(hi + 1));

    /// <summary>True when every value lies in [lo, hi] (the domain is a subset).</summary>
    public bool Within(long lo, long hi) => !IsEmpty && Min >= lo && Max <= hi;

    /// <summary>Enumerate every value (finite domains only). Used by labeling.</summary>
    public System.Collections.Generic.IEnumerable<long> Values()
    {
        for (int i = 0; i < _iv.Length; i += 2)
            for (long v = _iv[i]; v <= _iv[i + 1]; v++)
                yield return v;
    }

    /// <summary>The interval endpoints, lo/hi pairs in order. For projection.</summary>
    public System.Collections.Generic.IReadOnlyList<(long Lo, long Hi)> Intervals()
    {
        var r = new (long, long)[_iv.Length / 2];
        for (int i = 0; i < _iv.Length; i += 2) r[i / 2] = (_iv[i], _iv[i + 1]);
        return r;
    }

    // The Above/Below/Without/Intersect builders already emit sorted, disjoint
    // intervals; only the trailing length may differ. (Above/Below/Without/Intersect
    // never create adjacency that wasn't already absent, because they only shrink
    // existing intervals or drop them.)
    private static ClpfdDomain Make(long[] buf, int n)
    {
        if (n == 0) return Empty;
        if (n == buf.Length) return new ClpfdDomain(buf);
        var exact = new long[n];
        Array.Copy(buf, exact, n);
        return new ClpfdDomain(exact);
    }
}
