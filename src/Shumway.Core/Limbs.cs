using System.Numerics;
using System.Runtime.CompilerServices;

namespace Shumway.Core;

/// <summary>ADR-064: a big integer is a block of cells on the heap, a header
/// and its limbs, every cell tagged <see cref="Tag.RawInt"/> so the collector
/// treats each as a leaf. A limb is the 60-bit payload of a cell, little-endian
/// across the block, the top limb nonzero. The kernels take and produce limb
/// spans in that cell format, so a heap block and a scratch buffer read alike
/// and a result reaches the heap as a block copy.
///
/// <para>Every operand span is normalised (no top zero limb) unless a method
/// says otherwise, and every count returned is normalised too. A 60-bit limb
/// leaves four bits of headroom in each product and sum, which is what lets the
/// carry chains run without overflow tests.</para></summary>
public static class Limbs
{
    public const int Width = 60;
    private const long RawTag = (long)Tag.RawInt << Cell.TagShift;
    private const ulong Mask = (1UL << Width) - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read(Cell c) => (ulong)c.Data & Mask;

    /// <summary>A limb cell; <paramref name="limb"/> is below 2^60.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell Make(ulong limb) => new(RawTag | (long)limb);

    /// <summary>The header cell of a block: the limb count and the sign.</summary>
    public static Cell Header(int count, bool negative)
        => new(RawTag | ((long)count << 1) | (negative ? 1L : 0L));

    public static int Count(Cell header) => (int)((header.Data & (long)Mask) >> 1);
    public static bool Negative(Cell header) => (header.Data & 1L) != 0;

    /// <summary>The count of <paramref name="limbs"/> without its top zero limbs.</summary>
    public static int Normalize(ReadOnlySpan<Cell> limbs)
    {
        int n = limbs.Length;
        while (n > 0 && Read(limbs[n - 1]) == 0) n--;
        return n;
    }

    /// <summary>Magnitude order of two normalised spans.</summary>
    public static int Compare(ReadOnlySpan<Cell> a, ReadOnlySpan<Cell> b)
    {
        if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
        for (int i = a.Length - 1; i >= 0; i--)
        {
            ulong x = Read(a[i]), y = Read(b[i]);
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    public static bool Equal(ReadOnlySpan<Cell> a, ReadOnlySpan<Cell> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Data != b[i].Data) return false;
        return true;
    }

    /// <summary>r := a + b. <paramref name="r"/> holds at least
    /// max(a, b) + 1 limbs and may not overlap an operand. Returns the count.</summary>
    public static int Add(ReadOnlySpan<Cell> a, ReadOnlySpan<Cell> b, Span<Cell> r)
    {
        if (a.Length < b.Length) { var t = a; a = b; b = t; }
        ulong carry = 0;
        int i = 0;
        for (; i < b.Length; i++)
        {
            ulong t = Read(a[i]) + Read(b[i]) + carry;
            r[i] = Make(t & Mask);
            carry = t >> Width;
        }
        for (; i < a.Length; i++)
        {
            ulong t = Read(a[i]) + carry;
            r[i] = Make(t & Mask);
            carry = t >> Width;
        }
        if (carry != 0) r[i++] = Make(carry);
        return i;
    }

    /// <summary>r := a - b for a >= b. <paramref name="r"/> holds at least
    /// a's limbs and may not overlap an operand. Returns the count.</summary>
    public static int Sub(ReadOnlySpan<Cell> a, ReadOnlySpan<Cell> b, Span<Cell> r)
    {
        ulong borrow = 0;
        int i = 0;
        for (; i < b.Length; i++)
        {
            ulong x = Read(a[i]), y = Read(b[i]) + borrow;
            if (x >= y) { r[i] = Make(x - y); borrow = 0; }
            else { r[i] = Make(x + (1UL << Width) - y); borrow = 1; }
        }
        for (; i < a.Length; i++)
        {
            ulong x = Read(a[i]);
            if (x >= borrow) { r[i] = Make(x - borrow); borrow = 0; }
            else { r[i] = Make(x + (1UL << Width) - borrow); borrow = 1; }
        }
        return Normalize(r.Slice(0, a.Length));
    }

    /// <summary>r := a * m for a limb-sized <paramref name="m"/> (below 2^60,
    /// nonzero). <paramref name="r"/> holds at least a + 1 limbs and may not
    /// overlap the operand. Returns the count.</summary>
    public static int MulLimb(ReadOnlySpan<Cell> a, ulong m, Span<Cell> r)
    {
        ulong carry = 0;
        int i = 0;
        for (; i < a.Length; i++)
        {
            ulong hi = BigMul(Read(a[i]), m, out ulong lo);
            ulong t = (lo & Mask) + carry;
            r[i] = Make(t & Mask);
            carry = ((hi << 4) | (lo >> Width)) + (t >> Width);
        }
        if (carry != 0) r[i++] = Make(carry);
        return i;
    }

    /// <summary>r := a * ma + b * mb for limb-sized multipliers: the one pass
    /// a linear recurrence takes instead of three. <paramref name="r"/> holds at
    /// least max(a, b) + 2 limbs and may not overlap an operand. Returns the
    /// count.</summary>
    public static int MulAddMul(ReadOnlySpan<Cell> a, ulong ma, ReadOnlySpan<Cell> b, ulong mb, Span<Cell> r)
    {
        if (a.Length < b.Length) { var t = a; a = b; b = t; ulong m = ma; ma = mb; mb = m; }
        ulong carry = 0;
        int i = 0;
        for (; i < b.Length; i++)
        {
            ulong hiA = BigMul(Read(a[i]), ma, out ulong loA);
            ulong hiB = BigMul(Read(b[i]), mb, out ulong loB);
            ulong t = (loA & Mask) + (loB & Mask) + carry;
            r[i] = Make(t & Mask);
            carry = ((hiA << 4) | (loA >> Width)) + ((hiB << 4) | (loB >> Width)) + (t >> Width);
        }
        for (; i < a.Length; i++)
        {
            ulong hiA = BigMul(Read(a[i]), ma, out ulong loA);
            ulong t = (loA & Mask) + carry;
            r[i] = Make(t & Mask);
            carry = ((hiA << 4) | (loA >> Width)) + (t >> Width);
        }
        while (carry != 0)
        {
            r[i++] = Make(carry & Mask);
            carry >>= Width;
        }
        return i;
    }

    /// <summary>The operand size, in limbs, from which <see cref="Mul(ReadOnlySpan{Cell}, ReadOnlySpan{Cell}, Span{Cell})"/>
    /// splits its operands (Karatsuba) instead of running the schoolbook.</summary>
    public const int KaratsubaThreshold = 32;

    /// <summary>r := a * b. <paramref name="r"/> holds at least a + b limbs, is
    /// overwritten, and may not overlap an operand. Returns the count. Operands
    /// of <see cref="KaratsubaThreshold"/> limbs or more are split, recursively
    /// (Karatsuba); smaller ones run the schoolbook.</summary>
    public static int Mul(ReadOnlySpan<Cell> a, ReadOnlySpan<Cell> b, Span<Cell> r)
        => Mul(a, b, r, KaratsubaThreshold);

    /// <summary>The same with the split threshold given: the tests take it low
    /// to reach the recursion on small operands, and a measurement takes it
    /// past every size to time the schoolbook alone. A threshold below 4 is
    /// taken as 4, the smallest at which a split shrinks its operands.</summary>
    public static int Mul(ReadOnlySpan<Cell> a, ReadOnlySpan<Cell> b, Span<Cell> r, int threshold)
    {
        int n = a.Length + b.Length;
        MulFull(a, b, r.Slice(0, n), Math.Max(threshold, 4));
        return Normalize(r.Slice(0, n));
    }

    // r := a * b over exactly a + b limbs, not normalised. The operands need not
    // be normalised either: a split half can carry top zero limbs.
    private static void MulFull(ReadOnlySpan<Cell> a, ReadOnlySpan<Cell> b, Span<Cell> r, int threshold)
    {
        if (a.Length < b.Length) { var t = a; a = b; b = t; }
        if (b.Length < threshold) { Schoolbook(a, b, r); return; }
        if (a.Length == b.Length)
        {
            Karatsuba(a, b, r, new Cell[KaratsubaScratch(a.Length, threshold)], threshold);
            return;
        }
        // Unbalanced: a in pieces of b's length, each product added in at its offset.
        r.Fill(Make(0));
        int m = b.Length;
        var piece = new Cell[2 * m];
        for (int off = 0; off < a.Length; off += m)
        {
            int c = Math.Min(m, a.Length - off);
            Span<Cell> p = piece.AsSpan(0, c + m);
            MulFull(a.Slice(off, c), b, p, threshold);
            AddInPlace(r.Slice(off), p.Slice(0, Normalize(p)));
        }
    }

    // r := a * b over exactly a + b limbs, row by row.
    private static void Schoolbook(ReadOnlySpan<Cell> a, ReadOnlySpan<Cell> b, Span<Cell> r)
    {
        r.Slice(0, a.Length + b.Length).Fill(Make(0));
        for (int j = 0; j < b.Length; j++)
        {
            ulong m = Read(b[j]);
            if (m == 0) continue;
            ulong carry = 0;
            for (int i = 0; i < a.Length; i++)
            {
                ulong hi = BigMul(Read(a[i]), m, out ulong lo);
                ulong t = Read(r[i + j]) + (lo & Mask) + carry;
                r[i + j] = Make(t & Mask);
                carry = ((hi << 4) | (lo >> Width)) + (t >> Width);
            }
            r[j + a.Length] = Make(carry);
        }
    }

    // r := a * b over exactly 2n limbs for operands of n limbs each. With
    // a = a1*B^k + a0 and b likewise: z0 = a0*b0 and z2 = a1*b1 go straight to
    // their places in r, and z1 = (a0+a1)(b0+b1) - z0 - z2 is added in at k.
    // The scratch holds KaratsubaScratch(n) limbs: the two sums and z1, then
    // the scratch of the recursion that makes z1.
    private static void Karatsuba(ReadOnlySpan<Cell> a, ReadOnlySpan<Cell> b, Span<Cell> r,
        Span<Cell> scratch, int threshold)
    {
        int n = a.Length;
        if (n < threshold) { Schoolbook(a, b, r); return; }
        int k = (n + 1) / 2, h = n - k;
        Karatsuba(a.Slice(0, k), b.Slice(0, k), r.Slice(0, 2 * k), scratch, threshold);
        Karatsuba(a.Slice(k), b.Slice(k), r.Slice(2 * k, 2 * h), scratch, threshold);
        Span<Cell> sa = scratch.Slice(0, k + 1);
        Span<Cell> sb = scratch.Slice(k + 1, k + 1);
        Span<Cell> z1 = scratch.Slice(2 * k + 2, 2 * k + 2);
        sa[k] = Make(AddFixed(a.Slice(0, k), a.Slice(k), sa));
        sb[k] = Make(AddFixed(b.Slice(0, k), b.Slice(k), sb));
        Karatsuba(sa, sb, z1, scratch.Slice(4 * k + 4), threshold);
        SubInPlace(z1, r.Slice(0, 2 * k));
        SubInPlace(z1, r.Slice(2 * k, 2 * h));
        // The whole product fits in 2n limbs, so z1 shifted by k does too.
        AddInPlace(r.Slice(k), z1.Slice(0, Normalize(z1)));
    }

    /// <summary>r := a * a. <paramref name="r"/> holds at least 2a limbs, is
    /// overwritten, and may not overlap the operand. Returns the count. About
    /// half the work of <see cref="Mul(ReadOnlySpan{Cell}, ReadOnlySpan{Cell}, Span{Cell})"/>
    /// on the same operand twice: each cross product is computed once and
    /// doubled.</summary>
    public static int Square(ReadOnlySpan<Cell> a, Span<Cell> r) => Square(a, r, KaratsubaThreshold);

    /// <summary>The same with the split threshold given (see
    /// <see cref="Mul(ReadOnlySpan{Cell}, ReadOnlySpan{Cell}, Span{Cell}, int)"/>).</summary>
    public static int Square(ReadOnlySpan<Cell> a, Span<Cell> r, int threshold)
    {
        int n = 2 * a.Length;
        threshold = Math.Max(threshold, 4);
        var scratch = a.Length < threshold ? Array.Empty<Cell>() : new Cell[SquareScratch(a.Length, threshold)];
        KaratsubaSquare(a, r.Slice(0, n), scratch, threshold);
        return Normalize(r.Slice(0, n));
    }

    // r := a * a over exactly 2n limbs: the products below the diagonal once,
    // doubled, then the squares on the diagonal.
    private static void SchoolbookSquare(ReadOnlySpan<Cell> a, Span<Cell> r)
    {
        int n = a.Length;
        r.Slice(0, 2 * n).Fill(Make(0));
        for (int i = 0; i < n; i++)
        {
            ulong m = Read(a[i]);
            if (m == 0) continue;
            ulong carry = 0;
            for (int j = i + 1; j < n; j++)
            {
                ulong hi = BigMul(Read(a[j]), m, out ulong lo);
                ulong t = Read(r[i + j]) + (lo & Mask) + carry;
                r[i + j] = Make(t & Mask);
                carry = ((hi << 4) | (lo >> Width)) + (t >> Width);
            }
            r[i + n] = Make(carry);
        }
        ulong bit = 0;
        for (int p = 0; p < 2 * n; p++)
        {
            ulong t = (Read(r[p]) << 1) | bit;
            r[p] = Make(t & Mask);
            bit = t >> Width;
        }
        ulong c = 0;
        for (int i = 0; i < n; i++)
        {
            ulong x = Read(a[i]);
            ulong hi = BigMul(x, x, out ulong lo);
            ulong t = Read(r[2 * i]) + (lo & Mask) + c;
            r[2 * i] = Make(t & Mask);
            ulong t2 = Read(r[2 * i + 1]) + ((hi << 4) | (lo >> Width)) + (t >> Width);
            r[2 * i + 1] = Make(t2 & Mask);
            c = t2 >> Width;
        }
    }

    // Karatsuba on a square: z0 = a0^2, z2 = a1^2, z1 = (a0+a1)^2 - z0 - z2,
    // three squares where the product takes three products.
    private static void KaratsubaSquare(ReadOnlySpan<Cell> a, Span<Cell> r, Span<Cell> scratch, int threshold)
    {
        int n = a.Length;
        if (n < threshold) { SchoolbookSquare(a, r); return; }
        int k = (n + 1) / 2, h = n - k;
        KaratsubaSquare(a.Slice(0, k), r.Slice(0, 2 * k), scratch, threshold);
        KaratsubaSquare(a.Slice(k), r.Slice(2 * k, 2 * h), scratch, threshold);
        Span<Cell> s = scratch.Slice(0, k + 1);
        Span<Cell> z1 = scratch.Slice(k + 1, 2 * k + 2);
        s[k] = Make(AddFixed(a.Slice(0, k), a.Slice(k), s));
        KaratsubaSquare(s, z1, scratch.Slice(3 * k + 3), threshold);
        SubInPlace(z1, r.Slice(0, 2 * k));
        SubInPlace(z1, r.Slice(2 * k, 2 * h));
        AddInPlace(r.Slice(k), z1.Slice(0, Normalize(z1)));
    }

    private static int SquareScratch(int n, int threshold)
    {
        if (n < threshold) return 0;
        int k = (n + 1) / 2;
        return 3 * (k + 1) + Math.Max(SquareScratch(k, threshold), SquareScratch(k + 1, threshold));
    }

    private static int KaratsubaScratch(int n, int threshold)
    {
        if (n < threshold) return 0;
        int k = (n + 1) / 2;
        return 4 * (k + 1) + Math.Max(KaratsubaScratch(k, threshold), KaratsubaScratch(k + 1, threshold));
    }

    // dst := x + y over x's length, y no longer; returns the carry out.
    private static ulong AddFixed(ReadOnlySpan<Cell> x, ReadOnlySpan<Cell> y, Span<Cell> dst)
    {
        ulong carry = 0;
        int i = 0;
        for (; i < y.Length; i++)
        {
            ulong t = Read(x[i]) + Read(y[i]) + carry;
            dst[i] = Make(t & Mask);
            carry = t >> Width;
        }
        for (; i < x.Length; i++)
        {
            ulong t = Read(x[i]) + carry;
            dst[i] = Make(t & Mask);
            carry = t >> Width;
        }
        return carry;
    }

    // r += a, where the caller knows the sum fits in r: the carry never leaves it.
    private static void AddInPlace(Span<Cell> r, ReadOnlySpan<Cell> a)
    {
        ulong carry = 0;
        int i = 0;
        for (; i < a.Length; i++)
        {
            ulong t = Read(r[i]) + Read(a[i]) + carry;
            r[i] = Make(t & Mask);
            carry = t >> Width;
        }
        for (; carry != 0; i++)
        {
            ulong t = Read(r[i]) + carry;
            r[i] = Make(t & Mask);
            carry = t >> Width;
        }
    }

    // r -= a, where the caller knows r is no smaller: the borrow never leaves r.
    private static void SubInPlace(Span<Cell> r, ReadOnlySpan<Cell> a)
    {
        ulong borrow = 0;
        int i = 0;
        for (; i < a.Length; i++)
        {
            ulong x = Read(r[i]), y = Read(a[i]) + borrow;
            if (x >= y) { r[i] = Make(x - y); borrow = 0; }
            else { r[i] = Make(x + (1UL << Width) - y); borrow = 1; }
        }
        for (; borrow != 0; i++)
        {
            ulong x = Read(r[i]);
            if (x != 0) { r[i] = Make(x - 1); borrow = 0; }
            else r[i] = Make(Mask);
        }
    }

    /// <summary>r := a &lt;&lt; bits. <paramref name="r"/> holds at least
    /// a + bits / 60 + 1 limbs and may not overlap the operand. Returns the count.</summary>
    public static int ShiftLeft(ReadOnlySpan<Cell> a, int bits, Span<Cell> r)
    {
        if (a.Length == 0) return 0;
        int whole = bits / Width, rem = bits % Width;
        r.Slice(0, whole).Fill(Make(0));
        if (rem == 0)
        {
            a.CopyTo(r.Slice(whole, a.Length));
            return whole + a.Length;
        }
        ulong carry = 0;
        for (int i = 0; i < a.Length; i++)
        {
            ulong x = Read(a[i]);
            r[i + whole] = Make(((x << rem) & Mask) | carry);
            carry = x >> (Width - rem);
        }
        int n = whole + a.Length;
        if (carry != 0) r[n++] = Make(carry);
        return n;
    }

    /// <summary>r := a &gt;&gt; bits. <paramref name="r"/> holds at least a's
    /// limbs and may not overlap the operand. Returns the count, zero when every
    /// bit shifted out.</summary>
    public static int ShiftRight(ReadOnlySpan<Cell> a, int bits, Span<Cell> r)
    {
        int whole = bits / Width, rem = bits % Width;
        if (whole >= a.Length) return 0;
        int n = a.Length - whole;
        if (rem == 0)
        {
            a.Slice(whole, n).CopyTo(r.Slice(0, n));
            return Normalize(r.Slice(0, n));
        }
        for (int i = 0; i < n; i++)
        {
            ulong x = Read(a[i + whole]) >> rem;
            if (i + whole + 1 < a.Length)
                x |= (Read(a[i + whole + 1]) << (Width - rem)) & Mask;
            r[i] = Make(x);
        }
        return Normalize(r.Slice(0, n));
    }

    /// <summary>The largest divisor <see cref="DivRemSmall"/> takes: a limb
    /// splits into two 30-bit halves, each of which, under the remainder of
    /// the previous step, divides in 64-bit arithmetic.</summary>
    public const ulong SmallDivisorMax = (1UL << 30) - 1;

    /// <summary>q := a / d and the remainder, for <paramref name="d"/> in
    /// [1, <see cref="SmallDivisorMax"/>]. <paramref name="q"/> holds at least
    /// a's limbs and may be the operand itself. Returns the count.
    ///
    /// <para>Each 30-bit half divides by a multiply with the precomputed
    /// reciprocal of d rather than a hardware division: the estimate is at
    /// most one below the quotient, and one conditional subtraction corrects
    /// it (the dividend is below d * 2^30, the reciprocal is floor((2^64 - 1) / d)).</para></summary>
    public static int DivRemSmall(ReadOnlySpan<Cell> a, ulong d, Span<Cell> q, out ulong remainder)
    {
        const ulong HalfMask = (1UL << 30) - 1;
        ulong inv = ulong.MaxValue / d;
        ulong rem = 0;
        for (int i = a.Length - 1; i >= 0; i--)
        {
            ulong x = Read(a[i]);
            ulong qh = DivStep(ref rem, x >> 30, d, inv);
            ulong ql = DivStep(ref rem, x & HalfMask, d, inv);
            q[i] = Make((qh << 30) | ql);
        }
        remainder = rem;
        return Normalize(q.Slice(0, a.Length));
    }

    /// <summary>The remainder of a / d alone, for <paramref name="d"/> in
    /// [1, <see cref="SmallDivisorMax"/>]: the division without its quotient.</summary>
    public static ulong RemSmall(ReadOnlySpan<Cell> a, ulong d)
    {
        const ulong HalfMask = (1UL << 30) - 1;
        ulong inv = ulong.MaxValue / d;
        ulong rem = 0;
        for (int i = a.Length - 1; i >= 0; i--)
        {
            ulong x = Read(a[i]);
            DivStep(ref rem, x >> 30, d, inv);
            DivStep(ref rem, x & HalfMask, d, inv);
        }
        return rem;
    }

    // One 30-bit step of the short division: (rem * 2^30 + chunk) / d by the
    // reciprocal, the remainder left in rem.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong DivStep(ref ulong rem, ulong chunk, ulong d, ulong inv)
    {
        ulong cur = (rem << 30) | chunk;
        ulong qq = BigMul(cur, inv, out _);
        ulong r = cur - qq * d;
        if (r >= d) { qq++; r -= d; }
        rem = r;
        return qq;
    }

    /// <summary>The magnitude of <paramref name="v"/> as limbs, at most two.
    /// Returns the count (zero for zero).</summary>
    public static int FromLong(long v, Span<Cell> dst)
    {
        ulong m = v < 0 ? (ulong)(-(v + 1)) + 1UL : (ulong)v;
        if (m == 0) return 0;
        dst[0] = Make(m & Mask);
        if ((m >> Width) == 0) return 1;
        dst[1] = Make(m >> Width);
        return 2;
    }

    /// <summary>The value when it fits the 60-bit inline range of
    /// <see cref="Cell.Int"/>: the canonical form of ADR-013.</summary>
    public static bool TryToLong(ReadOnlySpan<Cell> limbs, bool negative, out long value)
    {
        if (limbs.Length == 0) { value = 0; return true; }
        if (limbs.Length > 1) { value = 0; return false; }
        ulong m = Read(limbs[0]);
        if (!negative && m <= (ulong)Cell.MaxInt60) { value = (long)m; return true; }
        if (negative && m <= (1UL << 59)) { value = -(long)m; return true; }
        value = 0;
        return false;
    }

    /// <summary>The magnitude of <paramref name="v"/> as little-endian bytes,
    /// the form <see cref="CountFor"/> and <see cref="FromBytes"/> take.</summary>
    public static byte[] MagnitudeBytes(BigInteger v) => BigInteger.Abs(v).ToByteArray();

    /// <summary>The most bits a big integer holds: the platform type's own cap
    /// on .NET, so a value past it is a resource error on every target, not
    /// only where the platform refuses to compute it.</summary>
    public const long MaxBits = int.MaxValue;

    /// <summary>The bit length of a magnitude given as little-endian bytes.</summary>
    public static long BitLength(byte[] bytes)
    {
        int top = bytes.Length - 1;
        while (top >= 0 && bytes[top] == 0) top--;
        if (top < 0) return 0;
        // The 64-bit overload: the only one the .NET Framework polyfill has.
        return (long)top * 8 + (64 - BitOperations.LeadingZeroCount((ulong)bytes[top]));
    }

    /// <summary>The bit length of normalised limbs.</summary>
    public static long BitLength(ReadOnlySpan<Cell> limbs)
        => limbs.Length == 0 ? 0
            : (long)(limbs.Length - 1) * Width + (64 - BitOperations.LeadingZeroCount(Read(limbs[limbs.Length - 1])));

    /// <summary>The exact limb count of a magnitude given as little-endian
    /// bytes, for one no longer than <see cref="MaxBits"/>.</summary>
    public static int CountFor(byte[] bytes) => (int)((BitLength(bytes) + Width - 1) / Width);

    /// <summary>Packs little-endian magnitude bytes into limbs. Returns the
    /// count, which is <see cref="CountFor"/> of the same bytes.</summary>
    public static int FromBytes(byte[] bytes, Span<Cell> dst)
    {
        int n = 0;
        // Two limbs are exactly fifteen bytes: a 64-bit word and a 56-bit one.
        int pairs = bytes.Length / 15;
        int k = 0;
        for (int p = 0; p < pairs; p++, k += 15)
        {
            ulong lo = ReadUInt64(bytes, k);
            ulong hi = ReadUInt56(bytes, k + 8);
            dst[n++] = Make(lo & Mask);
            dst[n++] = Make((lo >> Width) | (hi << 4));
        }
        ulong acc = 0;
        int accBits = 0;
        for (; k < bytes.Length; k++)
        {
            byte b = bytes[k];
            // The bits of b past position 63 fall off the shift; they are
            // exactly the bits this limb does not take, and the flush below
            // starts the next limb from them.
            acc |= (ulong)b << accBits;
            accBits += 8;
            if (accBits >= Width)
            {
                dst[n++] = Make(acc & Mask);
                int over = accBits - Width;
                acc = (ulong)b >> (8 - over);
                accBits = over;
            }
        }
        if (accBits > 0 && acc != 0) dst[n++] = Make(acc);
        while (n > 0 && Read(dst[n - 1]) == 0) n--;
        return n;
    }

    /// <summary>The value of a block's limbs and sign.</summary>
    public static BigInteger ToBigInteger(ReadOnlySpan<Cell> limbs, bool negative)
    {
        if (limbs.Length == 0) return BigInteger.Zero;
        // One zero byte past the magnitude keeps the two's-complement
        // constructor reading a positive number.
        int byteCount = (limbs.Length * Width + 7) / 8 + 1;
        byte[] bytes = new byte[byteCount];
        // Two limbs are exactly fifteen bytes: a 64-bit word and a 56-bit one.
        int pairs = limbs.Length / 2;
        int k = 0;
        for (int p = 0; p < pairs; p++, k += 15)
        {
            ulong l0 = Read(limbs[2 * p]), l1 = Read(limbs[2 * p + 1]);
            WriteUInt64(bytes, k, l0 | (l1 << Width));
            WriteUInt56(bytes, k + 8, l1 >> 4);
        }
        for (; k < byteCount - 1; k++)
        {
            int p = k * 8;
            int li = p / Width, off = p % Width;
            ulong v = Read(limbs[li]) >> off;
            if (off > Width - 8 && li + 1 < limbs.Length)
                v |= Read(limbs[li + 1]) << (Width - off);
            bytes[k] = (byte)v;
        }
        var m = new BigInteger(bytes);
        return negative ? -m : m;
    }

    // Little-endian words in a byte array, portable across the targets.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUInt64(byte[] b, int at)
        => b[at] | ((ulong)b[at + 1] << 8) | ((ulong)b[at + 2] << 16) | ((ulong)b[at + 3] << 24)
           | ((ulong)b[at + 4] << 32) | ((ulong)b[at + 5] << 40) | ((ulong)b[at + 6] << 48) | ((ulong)b[at + 7] << 56);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUInt56(byte[] b, int at)
        => b[at] | ((ulong)b[at + 1] << 8) | ((ulong)b[at + 2] << 16) | ((ulong)b[at + 3] << 24)
           | ((ulong)b[at + 4] << 32) | ((ulong)b[at + 5] << 40) | ((ulong)b[at + 6] << 48);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt64(byte[] b, int at, ulong v)
    {
        for (int i = 0; i < 8; i++) { b[at + i] = (byte)v; v >>= 8; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt56(byte[] b, int at, ulong v)
    {
        for (int i = 0; i < 7; i++) { b[at + i] = (byte)v; v >>= 8; }
    }

#if NETFRAMEWORK
    // Math.BigMul over two 64-bit words is not on .NET Framework: four 32-bit
    // products, the way the runtime's own portable path does it.
    private static ulong BigMul(ulong a, ulong b, out ulong low)
    {
        ulong al = (uint)a, ah = a >> 32, bl = (uint)b, bh = b >> 32;
        ulong ll = al * bl, lh = al * bh, hl = ah * bl, hh = ah * bh;
        ulong mid = (ll >> 32) + (uint)lh + (uint)hl;
        low = (mid << 32) | (uint)ll;
        return hh + (lh >> 32) + (hl >> 32) + (mid >> 32);
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong BigMul(ulong a, ulong b, out ulong low) => Math.BigMul(a, b, out low);
#endif
}
