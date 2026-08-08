using System.Numerics;

namespace Shumway.Core;

/// <summary>
/// An exact rational number <c>Num / Den</c>, always stored in canonical form:
/// <c>Den &gt; 0</c> and <c>gcd(|Num|, Den) == 1</c>. Constructed via
/// <see cref="Create"/>, which reduces and normalises the sign. A value whose
/// reduced denominator is 1 is an integer — callers building a cell / a
/// <c>Number</c> collapse that case to an integer rather than a rational
/// (see ADR-039: every <see cref="Tag.Rational"/> cell is a genuine fraction,
/// <c>Den != 1</c>). The struct itself permits <c>Den == 1</c> transiently so
/// arithmetic can produce it and the caller decide.
/// </summary>
public readonly struct Rational : IEquatable<Rational>
{
    public BigInteger Num { get; }
    public BigInteger Den { get; }

    private Rational(BigInteger num, BigInteger den)
    {
        Num = num;
        Den = den;
    }

    /// <summary>Reduces <paramref name="num"/>/<paramref name="den"/> to
    /// canonical form (den &gt; 0, gcd 1). Throws on a zero denominator.</summary>
    public static Rational Create(BigInteger num, BigInteger den)
    {
        if (den.IsZero)
            throw new PrologRuntimeException("evaluation_error", "zero_divisor");
        if (den.Sign < 0) { num = -num; den = -den; }
        BigInteger g = BigInteger.GreatestCommonDivisor(BigInteger.Abs(num), den);
        if (!g.IsOne && !g.IsZero) { num /= g; den /= g; }
        return new Rational(num, den);
    }

    /// <summary>True when the reduced denominator is 1 — the value is an
    /// integer and should be represented as one, not as a rational cell.</summary>
    public bool IsInteger => Den.IsOne;

    public double ToDouble() => (double)Num / (double)Den;

    public bool Equals(Rational other) => Num == other.Num && Den == other.Den;
    public override bool Equals(object? obj) => obj is Rational r && Equals(r);
#if NETFRAMEWORK
    public override int GetHashCode() => Compat.Combine(Num, Den);
#else
    public override int GetHashCode() => HashCode.Combine(Num, Den);
#endif
    public override string ToString() => $"{Num} rdiv {Den}";

    /// <summary>Sign of the value (-1, 0, +1). Den is always positive, so it is
    /// the sign of the numerator.</summary>
    public int Sign => Num.Sign;
}
