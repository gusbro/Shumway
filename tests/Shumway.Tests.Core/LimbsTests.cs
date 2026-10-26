using System.Numerics;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>ADR-064: the limb kernels against <see cref="BigInteger"/> as the
/// oracle. Random operands up to a few hundred limbs and the edges: carries
/// across every limb boundary (all-ones limbs), results that collapse to the
/// inline range at both ends, sign changes through subtraction, zero.</summary>
public class LimbsTests
{
    private static Cell[] Of(BigInteger v)
    {
        byte[] bytes = Limbs.MagnitudeBytes(v);
        var cells = new Cell[Limbs.CountFor(bytes)];
        int n = Limbs.FromBytes(bytes, cells);
        Assert.Equal(cells.Length, n);
        return cells;
    }

    private static BigInteger Value(ReadOnlySpan<Cell> limbs, bool negative = false)
        => Limbs.ToBigInteger(limbs, negative);

    private static void AssertNormalised(ReadOnlySpan<Cell> r)
    {
        foreach (Cell c in r) Assert.Equal(Tag.RawInt, c.Tag);
        if (r.Length > 0) Assert.NotEqual(0UL, Limbs.Read(r[^1]));
    }

    private static readonly BigInteger Limb = BigInteger.One << 60;

    private static IEnumerable<BigInteger> Operands(Random rng)
    {
        yield return 0;
        yield return 1;
        yield return Limb - 1;
        yield return Limb;
        yield return Limb + 1;
        yield return (Limb * Limb) - 1;
        yield return BigInteger.Pow(2, 59) - 1;
        yield return BigInteger.Pow(2, 59);
        yield return BigInteger.Pow(2, 600) - 1;        // ten all-ones limbs
        yield return BigInteger.Pow(2, 758);
        for (int i = 0; i < 40; i++)
        {
            int bytes = rng.Next(1, 200 * 60 / 8);
            var buf = new byte[bytes + 1];              // top byte zero: positive
            rng.NextBytes(buf);
            buf[^1] = 0;
            yield return new BigInteger(buf);
        }
    }

    [Fact]
    public void BitLengthsCountTheMagnitudesBits()
    {
        var rng = new Random(37);
        foreach (BigInteger v in Operands(rng))
        {
            long expected = 0;
            for (BigInteger x = v; x > 0; x >>= 1) expected++;
            Assert.Equal(expected, Limbs.BitLength(Limbs.MagnitudeBytes(v)));
            Assert.Equal(expected, Limbs.BitLength(Of(v)));
            Assert.Equal(expected, Limbs.BitLength(Limbs.MagnitudeBytes(-v)));
        }
    }

    [Fact]
    public void ConversionsRoundTripEveryOperand()
    {
        var rng = new Random(20261025);
        foreach (BigInteger v in Operands(rng))
        {
            Cell[] cells = Of(v);
            AssertNormalised(cells);
            Assert.Equal(v, Value(cells));
            Assert.Equal(-v, Value(cells, negative: true));
        }
    }

    [Fact]
    public void AddSubtractAndCompareMatchTheOracle()
    {
        var rng = new Random(7);
        var ops = Operands(rng).ToList();
        foreach (BigInteger a in ops)
        foreach (BigInteger b in ops)
        {
            Cell[] ca = Of(a), cb = Of(b);
            var r = new Cell[Math.Max(ca.Length, cb.Length) + 1];
            int n = Limbs.Add(ca, cb, r);
            AssertNormalised(r.AsSpan(0, n));
            Assert.Equal(a + b, Value(r.AsSpan(0, n)));

            Assert.Equal(a.CompareTo(b), Limbs.Compare(ca, cb));
            Assert.Equal(a == b, Limbs.Equal(ca, cb));

            if (a >= b)
            {
                var d = new Cell[Math.Max(ca.Length, 1)];
                int nd = Limbs.Sub(ca, cb, d);
                AssertNormalised(d.AsSpan(0, nd));
                Assert.Equal(a - b, Value(d.AsSpan(0, nd)));
            }
        }
    }

    [Fact]
    public void MultiplicationsMatchTheOracle()
    {
        var rng = new Random(11);
        var ops = Operands(rng).ToList();
        ulong[] smalls = { 1, 2, 6, 1UL << 59, (1UL << 60) - 1, 1234567890123456789UL & ((1UL << 60) - 1) };
        foreach (BigInteger a in ops)
        {
            Cell[] ca = Of(a);
            foreach (ulong m in smalls)
            {
                var r = new Cell[ca.Length + 1];
                int n = Limbs.MulLimb(ca, m, r);
                AssertNormalised(r.AsSpan(0, n));
                Assert.Equal(a * m, Value(r.AsSpan(0, n)));
            }
            foreach (BigInteger b in ops)
            {
                Cell[] cb = Of(b);
                var r = new Cell[ca.Length + cb.Length];
                int n = Limbs.Mul(ca, cb, r);
                AssertNormalised(r.AsSpan(0, n));
                Assert.Equal(a * b, Value(r.AsSpan(0, n)));

                var f = new Cell[Math.Max(ca.Length, cb.Length) + 2];
                int nf = Limbs.MulAddMul(ca, 6, cb, (1UL << 60) - 1, f);
                AssertNormalised(f.AsSpan(0, nf));
                Assert.Equal(a * 6 + b * (Limb - 1), Value(f.AsSpan(0, nf)));
            }
        }
    }

    private static BigInteger RandomLimbs(Random rng, int limbs)
    {
        var buf = new byte[limbs * Limbs.Width / 8 + 1];   // top byte zero: positive
        rng.NextBytes(buf);
        buf[^1] = 0;
        return new BigInteger(buf);
    }

    private static BigInteger AllOnes(int limbs) => (BigInteger.One << (Limbs.Width * limbs)) - 1;

    /// <summary>The split (Karatsuba) against the oracle. A low threshold puts
    /// the recursion on small operands, balanced and unbalanced, down to the
    /// smallest split; all-ones limbs carry out of every intermediate sum.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(Limbs.KaratsubaThreshold)]
    public void KaratsubaMatchesTheOracleAtEveryThreshold(int threshold)
    {
        var rng = new Random(17 + threshold);
        int[] sizes = { 1, 3, 4, 5, 7, 8, 9, 16, 17, 31, 32, 33, 63, 64, 65, 100, 129, 257 };
        foreach (int n in sizes)
        foreach (int m in sizes)
        {
            foreach (var (a, b) in new[]
            {
                (RandomLimbs(rng, n), RandomLimbs(rng, m)),
                (AllOnes(n), AllOnes(m)),
                (AllOnes(n), RandomLimbs(rng, m)),
            })
            {
                Cell[] ca = Of(a), cb = Of(b);
                var r = new Cell[ca.Length + cb.Length];
                int count = Limbs.Mul(ca, cb, r, threshold);
                AssertNormalised(r.AsSpan(0, count));
                Assert.Equal(a * b, Value(r.AsSpan(0, count)));
            }
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(Limbs.KaratsubaThreshold)]
    public void SquaresMatchTheOracleAtEveryThreshold(int threshold)
    {
        var rng = new Random(29 + threshold);
        foreach (int n in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 16, 17, 31, 32, 33, 63, 64, 65, 100, 129, 257, 1000 })
        foreach (BigInteger a in new[] { RandomLimbs(rng, n), AllOnes(n) })
        {
            Cell[] ca = Of(a);
            var r = new Cell[2 * ca.Length];
            int count = Limbs.Square(ca, r, threshold);
            AssertNormalised(r.AsSpan(0, count));
            Assert.Equal(a * a, Value(r.AsSpan(0, count)));
        }
        Assert.Equal(0, Limbs.Square(ReadOnlySpan<Cell>.Empty, Span<Cell>.Empty, threshold));
    }

    [Fact]
    public void KaratsubaMatchesTheOracleOnLargeOperands()
    {
        var rng = new Random(23);
        foreach (var (n, m) in new[] { (1000, 1000), (1500, 700), (2049, 2047), (4000, 130) })
        {
            BigInteger a = RandomLimbs(rng, n), b = RandomLimbs(rng, m);
            Cell[] ca = Of(a), cb = Of(b);
            var r = new Cell[ca.Length + cb.Length];
            int count = Limbs.Mul(ca, cb, r);
            AssertNormalised(r.AsSpan(0, count));
            Assert.Equal(a * b, Value(r.AsSpan(0, count)));
        }
    }

    [Fact]
    public void ShiftsMatchTheOracle()
    {
        var rng = new Random(3);
        int[] shifts = { 0, 1, 7, 59, 60, 61, 119, 120, 121, 333, 600, 601 };
        foreach (BigInteger a in Operands(rng))
        {
            Cell[] ca = Of(a);
            foreach (int s in shifts)
            {
                var l = new Cell[ca.Length + s / Limbs.Width + 1];
                int nl = Limbs.ShiftLeft(ca, s, l);
                AssertNormalised(l.AsSpan(0, nl));
                Assert.Equal(a << s, Value(l.AsSpan(0, nl)));

                var r = new Cell[Math.Max(ca.Length, 1)];
                int nr = Limbs.ShiftRight(ca, s, r);
                AssertNormalised(r.AsSpan(0, nr));
                Assert.Equal(a >> s, Value(r.AsSpan(0, nr)));
            }
        }
    }

    [Fact]
    public void DivisionBySmallDivisorsMatchesTheOracle()
    {
        var rng = new Random(13);
        ulong[] divisors = { 1, 2, 3, 7, 10, 1000003, (1UL << 29) + 5, Limbs.SmallDivisorMax };
        foreach (BigInteger a in Operands(rng))
        {
            Cell[] ca = Of(a);
            foreach (ulong d in divisors)
            {
                var q = new Cell[Math.Max(ca.Length, 1)];
                int n = Limbs.DivRemSmall(ca, d, q, out ulong r);
                AssertNormalised(q.AsSpan(0, n));
                BigInteger expectedQ = BigInteger.DivRem(a, d, out BigInteger expectedR);
                Assert.Equal(expectedQ, Value(q.AsSpan(0, n)));
                Assert.Equal(expectedR, (BigInteger)r);
                Assert.Equal(expectedR, (BigInteger)Limbs.RemSmall(ca, d));
            }
            // In place: the quotient may overwrite the operand.
            var copy = (Cell[])ca.Clone();
            int ni = Limbs.DivRemSmall(copy, 7, copy, out ulong ri);
            Assert.Equal(a / 7, Value(copy.AsSpan(0, ni)));
            Assert.Equal(a % 7, (BigInteger)ri);
        }
    }

    [Theory]
    [InlineData("0", false, true, 0L)]
    [InlineData("576460752303423487", false, true, 576460752303423487L)]    // 2^59 - 1
    [InlineData("576460752303423488", false, false, 0L)]                     // 2^59
    [InlineData("576460752303423488", true, true, -576460752303423488L)]     // -2^59 is MinInt60
    [InlineData("576460752303423489", true, false, 0L)]
    [InlineData("1152921504606846975", false, false, 0L)]                    // 2^60 - 1, one limb
    [InlineData("1152921504606846976", false, false, 0L)]                    // 2^60, two limbs
    public void TryToLongHonoursTheInlineRange(string magnitude, bool negative, bool fits, long expected)
    {
        Cell[] cells = Of(BigInteger.Parse(magnitude));
        Assert.Equal(fits, Limbs.TryToLong(cells, negative, out long value));
        if (fits) Assert.Equal(expected, value);
    }

    [Fact]
    public void FromLongCoversTheWholeRange()
    {
        Span<Cell> two = stackalloc Cell[2];
        foreach (long v in new[] { 0L, 1L, -1L, long.MaxValue, long.MinValue, 1L << 60, -(1L << 60), (1L << 60) - 1 })
        {
            int n = Limbs.FromLong(v, two);
            AssertNormalised(two.Slice(0, n));
            Assert.Equal(BigInteger.Abs(v), Value(two.Slice(0, n)));
        }
    }

    [Fact]
    public void HeadersRoundTripCountAndSign()
    {
        foreach (int count in new[] { 1, 2, 12641, 1 << 28 })
        foreach (bool negative in new[] { false, true })
        {
            Cell h = Limbs.Header(count, negative);
            Assert.Equal(Tag.RawInt, h.Tag);
            Assert.Equal(count, Limbs.Count(h));
            Assert.Equal(negative, Limbs.Negative(h));
        }
    }

    [Fact]
    public void NormalizeDropsTopZeroLimbsOnly()
    {
        var cells = new[] { Limbs.Make(5), Limbs.Make(0), Limbs.Make(0) };
        Assert.Equal(1, Limbs.Normalize(cells));
        Assert.Equal(0, Limbs.Normalize(new[] { Limbs.Make(0) }));
        Assert.Equal(2, Limbs.Normalize(new[] { Limbs.Make(0), Limbs.Make(1) }));
    }
}
