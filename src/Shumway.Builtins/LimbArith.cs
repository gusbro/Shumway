using System.Numerics;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>ADR-064: the signed arithmetic over limb values, on the kernels of
/// <see cref="Limbs"/>. A result lands in a buffer rented from a pool and the
/// <see cref="Number"/> that names it owns it; whoever consumes that Number
/// (<see cref="ArithmeticEvaluator.ApplyBin"/>, <see cref="ArithmeticEvaluator.ApplyRel"/>,
/// <see cref="Number.ToCell"/>) returns the buffer. The pool is thread-static like
/// the evaluation stack the values travel on, and it holds no state between
/// goals: only free buffers, kept at the largest sizes seen.
///
/// <para>A product of limbs by a limb-sized multiplier is not computed here:
/// it becomes a Scaled value, so that <c>a*x + b*y</c> runs as one fused pass
/// and a product that ends an evaluation is written straight onto the heap.
/// Any other operation computes a Scaled operand first
/// (<see cref="Materialize"/>).</para></summary>
internal static class LimbArith
{
    private const int PoolSize = 8;
    private const ulong LimbMax = (1UL << Limbs.Width) - 1;
    [ThreadStatic] private static Cell[]?[]? _free;
    [ThreadStatic] private static int _freeCount;

    internal static Cell[] Rent(int minCells)
    {
        var free = _free;
        if (free is not null)
        {
            for (int i = _freeCount - 1; i >= 0; i--)
            {
                var a = free[i]!;
                if (a.Length < minCells) continue;
                free[i] = free[--_freeCount];
                free[_freeCount] = null;
                return a;
            }
        }
        return new Cell[minCells + (minCells >> 2) + 8];
    }

    internal static void Return(Cell[] buffer)
    {
        var free = _free ??= new Cell[PoolSize][];
        int smallest = -1;
        for (int i = 0; i < _freeCount; i++)
        {
            if (ReferenceEquals(free[i], buffer)) return;   // already here: a value consumed twice
            if (smallest < 0 || free[i]!.Length < free[smallest]!.Length) smallest = i;
        }
        if (_freeCount < PoolSize) { free[_freeCount++] = buffer; return; }
        // Full: keep the larger buffers, which are the ones worth keeping.
        if (free[smallest]!.Length < buffer.Length) free[smallest] = buffer;
    }

    /// <summary>Returns <paramref name="n"/>'s pooled buffer unless
    /// <paramref name="keep"/> still names it (a Min/Max result, a sign flip, a
    /// deferred product over it).</summary>
    internal static void ReleaseUnless(in Number n, in Number keep)
    {
        if (!n.OwnsBuffer) return;
        if ((keep.IsLimbs || keep.IsScaled) && ReferenceEquals(keep.LimbWords, n.LimbWords)) return;
        Return(n.LimbWords!);
    }

    /// <summary>A Scaled value computed into a buffer of its own; any other
    /// value as it is. Consumes the operand.</summary>
    internal static Number Materialize(in Number n)
    {
        if (!n.IsScaled) return n;
        Number r = Computed(in n);
        n.Release();
        return r;
    }

    // The product of a Scaled value in a rented buffer; the operand is untouched.
    private static Number Computed(in Number scaled)
    {
        var va = scaled.LimbSpan;
        var dst = Rent(va.Length + 1);
        return Make(dst, Limbs.MulLimb(va, scaled.Scale, dst), scaled.LimbsNegative);
    }

    // The limb view of an integer operand with its sign and multiplier (1
    // unless Scaled): a limb value in place, an Int in the caller's two-cell
    // temp, a BigInteger (a literal, or a result that came back over the
    // bridge) into a rented buffer the caller returns.
    private static ReadOnlySpan<Cell> View(in Number n, Span<Cell> temp,
        out bool negative, out ulong scale, out Cell[]? rented)
    {
        rented = null;
        scale = 1;
        if (n.IsLimbs || n.IsScaled)
        {
            negative = n.LimbsNegative;
            if (n.IsScaled) scale = n.Scale;
            return n.LimbSpan;
        }
        if (n.IsInt)
        {
            negative = n.IntValue < 0;
            return temp.Slice(0, Limbs.FromLong(n.IntValue, temp));
        }
        BigInteger v = n.BigValue;
        negative = v.Sign < 0;
        byte[] bytes = Limbs.MagnitudeBytes(v);
        rented = Rent(Limbs.CountFor(bytes));
        return new ReadOnlySpan<Cell>(rented, 0, Limbs.FromBytes(bytes, rented));
    }

    private static Number Make(Cell[] dst, int count, bool negative)
    {
        var r = new Number(dst, 0, count, negative, pooled: true);
        if (!r.IsLimbs) Return(dst);   // collapsed to the inline form
        return r;
    }

    public static Number Add(in Number a, in Number b) => AddSigned(in a, in b, negateB: false);
    public static Number Sub(in Number a, in Number b) => AddSigned(in a, in b, negateB: true);

    private static Number AddSigned(in Number a, in Number b, bool negateB)
    {
        Span<Cell> ta = stackalloc Cell[2];
        Span<Cell> tb = stackalloc Cell[2];
        var va = View(in a, ta, out bool na, out ulong ma, out var ra);
        var vb = View(in b, tb, out bool nb, out ulong mb, out var rb);
        if (negateB) nb = !nb;
        Number result;
        if (na == nb)
        {
            if (ma == 1 && mb == 1)
            {
                var dst = Rent(Math.Max(va.Length, vb.Length) + 1);
                result = Make(dst, Limbs.Add(va, vb, dst), na);
            }
            else
            {
                // The fused pass: the sum of two products in one walk.
                var dst = Rent(Math.Max(va.Length, vb.Length) + 2);
                result = Make(dst, Limbs.MulAddMul(va, ma, vb, mb, dst), na);
            }
        }
        else
        {
            // Opposite signs: a difference of magnitudes, the products computed first.
            Cell[]? pa = null, pb = null;
            if (ma != 1) { pa = Rent(va.Length + 1); va = new ReadOnlySpan<Cell>(pa, 0, Limbs.MulLimb(va, ma, pa)); }
            if (mb != 1) { pb = Rent(vb.Length + 1); vb = new ReadOnlySpan<Cell>(pb, 0, Limbs.MulLimb(vb, mb, pb)); }
            int c = Limbs.Compare(va, vb);
            if (c == 0) result = new Number(0L);
            else
            {
                var dst = Rent(Math.Max(va.Length, vb.Length));
                int n = c > 0 ? Limbs.Sub(va, vb, dst) : Limbs.Sub(vb, va, dst);
                result = Make(dst, n, c > 0 ? na : nb);
            }
            if (pa is not null) Return(pa);
            if (pb is not null) Return(pb);
        }
        if (ra is not null) Return(ra);
        if (rb is not null) Return(rb);
        return result;
    }

    public static Number Mul(in Number a, in Number b)
    {
        // A limb-sized multiplier against limbs is deferred (D3): the sum that
        // usually follows fuses it, and a final one is written onto the heap.
        if (a.IsInt && (b.IsLimbs || b.IsScaled)) return ScaleBy(in b, a.IntValue);
        if (b.IsInt && (a.IsLimbs || a.IsScaled)) return ScaleBy(in a, b.IntValue);
        Number am = Materialize(in a), bm = Materialize(in b);
        Span<Cell> ta = stackalloc Cell[2];
        Span<Cell> tb = stackalloc Cell[2];
        var va = View(in am, ta, out bool na, out _, out var ra);
        var vb = View(in bm, tb, out bool nb, out _, out var rb);
        Number result;
        if (va.Length == 0 || vb.Length == 0) result = new Number(0L);
        else if (vb.Length == 1)
        {
            var dst = Rent(va.Length + 1);
            result = Make(dst, Limbs.MulLimb(va, Limbs.Read(vb[0]), dst), na != nb);
        }
        else if (va.Length == 1)
        {
            var dst = Rent(vb.Length + 1);
            result = Make(dst, Limbs.MulLimb(vb, Limbs.Read(va[0]), dst), na != nb);
        }
        else if (va == vb)
        {
            // One block on both sides (X*X): the square kernel, half the work.
            // The signs can still differ (-X*X views the block negated).
            var dst = Rent(2 * va.Length);
            result = Make(dst, Limbs.Square(va, dst), na != nb);
        }
        else
        {
            var dst = Rent(va.Length + vb.Length);
            result = Make(dst, Limbs.Mul(va, vb, dst), na != nb);
        }
        if (ra is not null) Return(ra);
        if (rb is not null) Return(rb);
        // A materialised operand is a value of this method's own; the caller
        // releases only what it passed.
        if (a.IsScaled) ReleaseUnless(in am, in result);
        if (b.IsScaled) ReleaseUnless(in bm, in result);
        return result;
    }

    private static Number ScaleBy(in Number limbs, long m)
    {
        if (m == 0)
        {
            limbs.Release();
            return new Number(0L);
        }
        ulong mag = m < 0 ? (ulong)(-(m + 1)) + 1UL : (ulong)m;   // an Int: below 2^59
        bool negative = limbs.LimbsNegative ^ (m < 0);
        if (!limbs.IsScaled) return new Number(in limbs, mag, negative);
        // Two multipliers combine while their product stays limb-sized.
        if (limbs.Scale <= LimbMax / mag) return new Number(in limbs, limbs.Scale * mag, negative);
        Number computed = Materialize(in limbs);
        return new Number(in computed, mag, negative);
    }

    /// <summary>Whether <paramref name="d"/> is a divisor the linear kernel
    /// takes: nonzero and below 2^30 in magnitude.</summary>
    public static bool IsSmallDivisor(long d) => d != 0 && d > -(long)Limbs.SmallDivisorMax && d < (long)Limbs.SmallDivisorMax;

    /// <summary>The ISO integer divisions of a Limbs value by a small divisor:
    /// one linear pass for the magnitudes, the signs fixed per operation.
    /// <paramref name="floor"/> rounds the quotient toward minus infinity
    /// (<c>div</c>) rather than toward zero (<c>//</c>).</summary>
    public static Number Quotient(in Number a, long d, bool floor)
    {
        var va = a.LimbSpan;
        var dst = Rent(va.Length + 1);
        int n = Limbs.DivRemSmall(va, Magnitude(d), dst, out ulong r);
        bool negative = a.LimbsNegative ^ (d < 0);
        if (floor && negative && r != 0)
        {
            // Toward minus infinity: one past the truncated magnitude.
            Span<Cell> one = stackalloc Cell[1];
            one[0] = Limbs.Make(1);
            var sum = Rent(n + 1);
            int ns = Limbs.Add(dst.AsSpan(0, n), one, sum);
            Return(dst);
            return Make(sum, ns, negative);
        }
        return Make(dst, n, negative);
    }

    /// <summary>The ISO remainders of a Limbs value by a small divisor: <c>rem</c>
    /// takes the dividend's sign, <c>mod</c> (<paramref name="floorMod"/>) the
    /// divisor's.</summary>
    public static Number Remainder(in Number a, long d, bool floorMod)
    {
        ulong r = Limbs.RemSmall(a.LimbSpan, Magnitude(d));
        if (r == 0) return new Number(0L);
        if (!floorMod) return new Number(a.LimbsNegative ? -(long)r : (long)r);
        if (a.LimbsNegative == (d < 0)) return new Number(d < 0 ? -(long)r : (long)r);
        long away = (long)(Magnitude(d) - r);
        return new Number(d < 0 ? -away : away);
    }

    private static ulong Magnitude(long d) => d < 0 ? (ulong)(-(d + 1)) + 1UL : (ulong)d;

    /// <summary><paramref name="a"/> is a Limbs value; the sign rides along.</summary>
    public static Number ShiftLeft(in Number a, int bits)
    {
        var va = a.LimbSpan;
        var dst = Rent(va.Length + bits / Limbs.Width + 1);
        return Make(dst, Limbs.ShiftLeft(va, bits, dst), a.LimbsNegative);
    }

    /// <summary><paramref name="a"/> is a non-negative Limbs value: a negative
    /// one rounds toward minus infinity, which the caller leaves to the bridge.</summary>
    public static Number ShiftRight(in Number a, int bits)
    {
        var va = a.LimbSpan;
        var dst = Rent(va.Length);
        return Make(dst, Limbs.ShiftRight(va, bits, dst), negative: false);
    }

    /// <summary>Order of two integer values, at least one of them in limbs.
    /// Consumes nothing.</summary>
    public static int Compare(in Number a, in Number b)
    {
        if (a.IsScaled || b.IsScaled)
        {
            Number am = a.IsScaled ? Computed(in a) : a;
            Number bm = b.IsScaled ? Computed(in b) : b;
            int r = Compare(in am, in bm);
            if (a.IsScaled) am.Release();
            if (b.IsScaled) bm.Release();
            return r;
        }
        if (a.IsLimbs && b.IsLimbs)
        {
            if (a.LimbsNegative != b.LimbsNegative) return a.LimbsNegative ? -1 : 1;
            int c = Limbs.Compare(a.LimbSpan, b.LimbSpan);
            return a.LimbsNegative ? -c : c;
        }
        // A Limbs value lies outside the inline range, so its sign orders it
        // against every Int.
        if (a.IsLimbs && b.IsInt) return a.LimbsNegative ? -1 : 1;
        if (b.IsLimbs && a.IsInt) return b.LimbsNegative ? 1 : -1;
        return a.BigValue.CompareTo(b.BigValue);
    }
}
