using System;
using System.Numerics;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>DoubleConversion gives the nearest double, ties to even, checked
/// against exact rational distances to the result's two neighbours (no
/// runtime's parser as the oracle: .NET Framework's does not round correctly).</summary>
public class DoubleConversionTests
{
    private static readonly BigInteger Two = 2;

    [Fact]
    public void ABignumConvertsAsALongOfTheSameValueDoes()
    {
        var x = BigInteger.Pow(Two, 61) + 257;
        Assert.Equal((double)(long)x, DoubleConversion.FromInteger(x));
        Assert.Equal(2305843009213694464d, DoubleConversion.FromInteger(x));
        Assert.Equal(-2305843009213694464d, DoubleConversion.FromInteger(-x));
    }

    [Fact]
    public void IntegersRoundToTheNearestDouble()
    {
        foreach (var x in Integers())
        {
            AssertNearest(x, BigInteger.One, DoubleConversion.FromInteger(x));
            AssertNearest(-x, BigInteger.One, DoubleConversion.FromInteger(-x));
        }
    }

    [Fact]
    public void RatiosRoundToTheNearestDouble()
    {
        foreach (var (n, d) in Ratios())
        {
            AssertNearest(n, d, DoubleConversion.FromRatio(n, d));
            AssertNearest(-n, d, DoubleConversion.FromRatio(-n, d));
        }
    }

    [Fact]
    public void TheEdgesOfTheDoubleRange()
    {
        var p1024 = BigInteger.Pow(Two, 1024);
        var halfway = p1024 - BigInteger.Pow(Two, 970);     // between the largest double and 2^1024
        Assert.Equal(double.MaxValue, DoubleConversion.FromInteger(halfway - 1));
        Assert.Equal(double.PositiveInfinity, DoubleConversion.FromInteger(halfway));
        Assert.Equal(double.NegativeInfinity, DoubleConversion.FromInteger(-halfway));
        var p1074 = BigInteger.Pow(Two, 1074);
        Assert.Equal(double.Epsilon, DoubleConversion.FromRatio(1, p1074));
        Assert.Equal(0d, DoubleConversion.FromRatio(1, p1074 * 2));       // a tie: 0 is even
        Assert.Equal(double.Epsilon, DoubleConversion.FromRatio(3, p1074 * 4));
        Assert.Equal(0d, DoubleConversion.FromRatio(1, p1074 * 8));
        // Numerator and denominator each past the double range, quotient not.
        var big = BigInteger.Pow(10, 400);
        Assert.Equal(1d / 3, DoubleConversion.FromRatio(big, 3 * big));
    }

    private static System.Collections.Generic.IEnumerable<BigInteger> Integers()
    {
        // Around a halfway point, at several magnitudes: below, on (ties to
        // even, both ways) and above it.
        foreach (int k in new[] { 54, 60, 61, 62, 63, 64, 65, 100, 500, 1000, 1023 })
        {
            var p = BigInteger.Pow(Two, k);
            var half = BigInteger.Pow(Two, k - 53);
            foreach (var x in new[] { p, p + half - 1, p + half, p + half + 1, p + 3 * half,
                                      p + 3 * half - 1, p + 3 * half + 1, 2 * p - 1 })
                yield return x;
        }
        var rng = new Random(12345);
        for (int i = 0; i < 2000; i++) yield return RandomBits(rng, 54 + rng.Next(1000));
    }

    private static System.Collections.Generic.IEnumerable<(BigInteger, BigInteger)> Ratios()
    {
        yield return (1, 3);
        yield return (2, 3);
        yield return (BigInteger.Pow(Two, 60) + 1, 3);
        yield return (BigInteger.Pow(Two, 1100), 3);
        yield return (1, BigInteger.Pow(Two, 1050) * 3);           // subnormal
        yield return (BigInteger.Pow(10, 300) + 7, BigInteger.Pow(10, 300));
        var rng = new Random(54321);
        for (int i = 0; i < 2000; i++)
        {
            var n = RandomBits(rng, 1 + rng.Next(1200));
            var d = RandomBits(rng, 1 + rng.Next(1200));
            yield return (n, d);
        }
    }

    private static BigInteger RandomBits(Random rng, int bits)
    {
        var bytes = new byte[bits / 8 + 2];
        rng.NextBytes(bytes);
        bytes[bytes.Length - 1] = 0;                               // positive
        var x = new BigInteger(bytes) % BigInteger.Pow(Two, bits);
        return x | BigInteger.Pow(Two, bits - 1);                  // exactly `bits` long
    }

    /// <summary>r is the double nearest n/d (d &gt; 0), ties to even.</summary>
    private static void AssertNearest(BigInteger n, BigInteger d, double r)
    {
        string what = $"{n} / {d} -> {r:R}";
        if (n.Sign < 0) { AssertNearest(-n, d, -r); return; }
        Assert.False(double.IsNaN(r), what);
        Assert.True(r >= 0, what);
        if (double.IsPositiveInfinity(r))
        {
            // At or past the halfway point between the largest double and 2^1024.
            var halfway = BigInteger.Pow(Two, 1024) - BigInteger.Pow(Two, 970);
            Assert.True(n >= halfway * d, what);
            return;
        }
        long bits = BitConverter.DoubleToInt64Bits(r);
        int cmpUp = CompareDistance(n, d, r, BitConverter.Int64BitsToDouble(bits + 1));
        Assert.True(cmpUp < 0 || (cmpUp == 0 && (bits & 1) == 0), what + " (the next double up is nearer)");
        if (bits > 0)
        {
            int cmpDown = CompareDistance(n, d, r, BitConverter.Int64BitsToDouble(bits - 1));
            Assert.True(cmpDown < 0 || (cmpDown == 0 && (bits & 1) == 0), what + " (the next double down is nearer)");
        }
    }

    /// <summary>The sign of |n/d - a| - |n/d - b|, exactly.</summary>
    private static int CompareDistance(BigInteger n, BigInteger d, double a, double b)
    {
        if (double.IsInfinity(b)) return -1;
        var (an, ad) = Exact(a);
        var (bn, bd) = Exact(b);
        var da = BigInteger.Abs(n * ad - an * d) * bd;
        var db = BigInteger.Abs(n * bd - bn * d) * ad;
        return da.CompareTo(db);
    }

    /// <summary>A finite non-negative double as num / den, den a power of two.</summary>
    private static (BigInteger Num, BigInteger Den) Exact(double x)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & ((1L << 52) - 1);
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        int e = exp - 1075;                                        // x = mant * 2^e
        return e >= 0 ? (mant * BigInteger.Pow(Two, e), BigInteger.One)
                      : (new BigInteger(mant), BigInteger.Pow(Two, -e));
    }
}
