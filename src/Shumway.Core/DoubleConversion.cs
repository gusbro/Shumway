using System.Numerics;

namespace Shumway.Core;

/// <summary>
/// Exact numbers converted to the nearest double, ties to even. Not
/// BigInteger's own conversion, which truncates: a bignum and a 60-bit integer
/// of the same magnitude must give the same float.
/// </summary>
public static class DoubleConversion
{
    private static readonly BigInteger TwoTo53 = BigInteger.One << 53;

    /// <summary>The double nearest <paramref name="value"/>; an infinity past
    /// the largest double.</summary>
    public static double FromInteger(BigInteger value)
    {
        if (value >= long.MinValue && value <= long.MaxValue) return (long)value;
        return Round(value.Sign < 0, BigInteger.Abs(value), sticky: false, exp2: 0);
    }

    /// <summary>The double nearest <paramref name="num"/> / <paramref name="den"/>,
    /// with <paramref name="den"/> positive.</summary>
    public static double FromRatio(BigInteger num, BigInteger den)
    {
        if (num.IsZero) return 0.0;
        var a = BigInteger.Abs(num);
        // Both exact as doubles, so one IEEE division rounds once.
        if (a <= TwoTo53 && den <= TwoTo53) return (double)num / (double)den;
        // A quotient of 65 or 66 bits: the bits a double keeps, the rounding
        // bit, and more, with the remainder's being nonzero as the sticky bit.
        int shift = 65 - (BitLength(a) - BitLength(den));
        BigInteger q, r;
        if (shift >= 0) q = BigInteger.DivRem(a << shift, den, out r);
        else q = BigInteger.DivRem(a, den << -shift, out r);
        return Round(num.Sign < 0, q, sticky: !r.IsZero, exp2: -shift);
    }

    /// <summary>±(m + ε)·2^<paramref name="exp2"/> rounded to the nearest double,
    /// ties to even, where m is positive and ε, below m's last bit, is nonzero
    /// exactly when <paramref name="sticky"/>. With <paramref name="sticky"/>
    /// set, m must carry more bits than the double keeps.</summary>
    private static double Round(bool negative, BigInteger m, bool sticky, int exp2)
    {
        int len = BitLength(m);
        int e = len - 1 + exp2;                         // the value's binary exponent
        // A normal double keeps 53 bits; below 2^-1022 the subnormals keep fewer,
        // down to none under half the least of them.
        int precision = e >= -1022 ? 53 : e + 1075;
        long bits;
        if (precision < 0) bits = 0;
        else
        {
            int drop = len - precision;
            ulong kept;
            if (drop <= 0) kept = (ulong)(m << -drop);
            else
            {
                kept = (ulong)(m >> drop);
                int cmp = (m - ((BigInteger)kept << drop)).CompareTo(BigInteger.One << (drop - 1));
                if (cmp > 0 || (cmp == 0 && (sticky || (kept & 1) != 0))) kept++;
            }
            if (e >= -1022)
            {
                if (kept == 1UL << 53) { kept >>= 1; e++; }
                if (e > 1023) return negative ? double.NegativeInfinity : double.PositiveInfinity;
                bits = ((long)(e + 1023) << 52) | (long)(kept & ((1UL << 52) - 1));
            }
            // A subnormal's significand is its bit pattern; rounding up to 2^52
            // carries into the least normal.
            else bits = (long)kept;
        }
        double d = System.BitConverter.Int64BitsToDouble(bits);
        return negative ? -d : d;
    }

    /// <summary>The bit length of a positive <paramref name="m"/>.</summary>
    private static int BitLength(BigInteger m)
    {
#if NET5_0_OR_GREATER
        return (int)m.GetBitLength();
#else
        byte[] b = m.ToByteArray();
        int top = b.Length - 1;
        if (b[top] == 0) top--;                         // a positive value's sign byte
        int bits = 0;
        for (int v = b[top]; v != 0; v >>= 1) bits++;
        return top * 8 + bits;
#endif
    }
}
