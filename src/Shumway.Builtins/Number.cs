using System.Globalization;
using System.Numerics;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// The runtime value type used by the arithmetic evaluator: an integer (within
/// <see cref="long"/> range), a <see cref="BigInteger"/> for results that
/// overflowed, or a double. Promotion is automatic: any <c>+</c> / <c>-</c> /
/// <c>*</c> overflowing long retries with BigInteger; any operand being a
/// float floats the whole expression.
/// </summary>
public readonly struct Number : IEquatable<Number>
{
    public enum Kind : byte { Int, Big, Float, Rat }

    public Kind ValueKind { get; }
    public long IntValue { get; }
    public BigInteger BigValue { get; }
    public double FloatValue { get; }
    public Rational RatValue { get; }

    public bool IsFloat => ValueKind == Kind.Float;
    public bool IsBig => ValueKind == Kind.Big;
    public bool IsInt => ValueKind == Kind.Int;
    public bool IsRat => ValueKind == Kind.Rat;

    /// <summary>An exact number viewed as a rational (num/den). Valid for Int,
    /// Big and Rat — an integer is num/1. Throws for a float.</summary>
    public (BigInteger Num, BigInteger Den) AsRationalParts() => ValueKind switch
    {
        Kind.Rat => (RatValue.Num, RatValue.Den),
        Kind.Big => (BigValue, BigInteger.One),
        Kind.Int => (new BigInteger(IntValue), BigInteger.One),
        _ => throw new InvalidOperationException("Number is a float; no rational view."),
    };

    /// <summary>Builds a Number from a rational, collapsing an integral value to
    /// Int/Big so a rational Number is always a genuine fraction.</summary>
    public Number(Rational rat)
    {
        if (rat.IsInteger)
        {
            // Route through the BigInteger ctor so it collapses to inline int
            // when it fits — one canonical form per value.
            var asBig = new Number(rat.Num);
            ValueKind = asBig.ValueKind;
            IntValue = asBig.IntValue;
            BigValue = asBig.BigValue;
            FloatValue = asBig.FloatValue;
            RatValue = default;
        }
        else
        {
            ValueKind = Kind.Rat;
            IntValue = 0;
            BigValue = default;
            FloatValue = rat.ToDouble();
            RatValue = rat;
        }
    }

    public Number(long intValue)
    {
        // Anything outside the 60-bit inline range has to live on the
        // BigInteger side table even though it fits in a long — Cell.Int
        // refuses to encode values that don't fit in the 60-bit payload.
        if (intValue < Cell.MinInt60 || intValue > Cell.MaxInt60)
        {
            ValueKind = Kind.Big;
            IntValue = 0;
            BigValue = new BigInteger(intValue);
            FloatValue = intValue;
        }
        else
        {
            ValueKind = Kind.Int;
            IntValue = intValue;
            BigValue = default;
            FloatValue = intValue;
        }
        RatValue = default;
    }

    public Number(BigInteger bigValue)
    {
        // Collapse to inline int when it fits in the 60-bit range —
        // keeps the cell path on the fast track for results that *would*
        // have fit if computed directly without overflow.
        if (bigValue >= Cell.MinInt60 && bigValue <= Cell.MaxInt60)
        {
            ValueKind = Kind.Int;
            IntValue = (long)bigValue;
            BigValue = default;
            FloatValue = IntValue;
        }
        else
        {
            ValueKind = Kind.Big;
            IntValue = 0;
            BigValue = bigValue;
            FloatValue = (double)bigValue;
        }
        RatValue = default;
    }

    public Number(double floatValue)
    {
        ValueKind = Kind.Float;
        IntValue = 0;
        BigValue = default;
        FloatValue = floatValue;
        RatValue = default;
    }

    /// <summary>The value as a double — exact for integers within
    /// <see cref="long"/> precision; lossy for BigInteger results that
    /// don't round-trip through double.</summary>
    public double AsDouble() => ValueKind switch
    {
        Kind.Float => FloatValue,
        Kind.Big => (double)BigValue,
        Kind.Rat => RatValue.ToDouble(),
        _ => IntValue,
    };

    /// <summary>The value as a <see cref="BigInteger"/>. Throws when the
    /// number is a float — arithmetic that mixes ints and floats produces
    /// a float result, so callers asking for a BigInteger have already
    /// checked the integer-only path.</summary>
    public BigInteger AsBigInteger() => ValueKind switch
    {
        Kind.Big => BigValue,
        Kind.Int => new BigInteger(IntValue),
        _ => throw new InvalidOperationException("Number is a float; no BigInteger view."),
    };

    /// <summary>Materialises the number as a heap cell. Inline-range ints go
    /// in <see cref="Cell.Int"/>; BigIntegers are stored in the engine's
    /// big-int side table and the returned cell is a <see cref="Cell.BigInt"/>;
    /// floats are placed on the heap via <see cref="Activation.MakeFloat"/> and
    /// the returned cell is a REF to the header.</summary>
    public Cell ToCell(Activation engine) => ValueKind switch
    {
        Kind.Float => Cell.Ref(engine.MakeFloat(FloatValue)),
        Kind.Big => engine.MakeBigInt(BigValue),
        Kind.Rat => engine.MakeRational(RatValue),
        _ => Cell.Int(IntValue),
    };

    public bool Equals(Number other) => ValueKind == other.ValueKind && ValueKind switch
    {
        Kind.Float => FloatValue == other.FloatValue,
        Kind.Big => BigValue.Equals(other.BigValue),
        Kind.Rat => RatValue.Equals(other.RatValue),
        _ => IntValue == other.IntValue,
    };

    public override bool Equals(object? obj) => obj is Number n && Equals(n);
    public override int GetHashCode() => ValueKind switch
    {
        Kind.Float => FloatValue.GetHashCode(),
        Kind.Big => BigValue.GetHashCode(),
        Kind.Rat => RatValue.GetHashCode(),
        _ => IntValue.GetHashCode(),
    };

    public override string ToString() => ValueKind switch
    {
        Kind.Float => FormatPrologFloat(FloatValue),
        Kind.Big => BigValue.ToString(CultureInfo.InvariantCulture),
        Kind.Rat => RatValue.ToString(),
        _ => IntValue.ToString(CultureInfo.InvariantCulture),
    };

    private static readonly char[] ExpChars = { 'e', 'E' };

    /// <summary>Formats a double as a round-trippable ISO Prolog float: the
    /// mantissa always carries a decimal point and the exponent uses a
    /// lowercase <c>e</c> with an EXPLICIT sign. .NET's <c>"R"</c> format
    /// emits forms like <c>1E-05</c> (no point, uppercase E) and <c>1</c>
    /// (for 1.0) that Shumway's own lexer reads back as an integer + a
    /// variable, not a float — so <c>writeq</c>/<c>write_canonical</c>
    /// output containing a small/large or whole-valued float was not
    /// re-consultable. This produces <c>1.0e-05</c> / <c>1.0</c> /
    /// <c>1.0e+300</c>. The positive exponent keeps its <c>+</c> because
    /// that is what SWI / GProlog / SICStus print and third-party code
    /// parses the printed form positionally (Logtalk's cbor splits at
    /// <c>e</c> and assumes a sign character follows).</summary>
    public static string FormatPrologFloat(double v)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsPositiveInfinity(v)) return "inf";
        if (double.IsNegativeInfinity(v)) return "-inf";
        string s = v.ToString("R", CultureInfo.InvariantCulture);
        int e = s.IndexOfAny(ExpChars);
        if (e < 0)
            return s.IndexOf('.') < 0 ? s + ".0" : s;
        string mant = s[..e];
        string exp = s[(e + 1)..];
        if (mant.IndexOf('.') < 0) mant += ".0";
        if (exp[0] != '+' && exp[0] != '-') exp = "+" + exp;
        return mant + "e" + exp;
    }

    public static int Compare(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat)
            return a.AsDouble().CompareTo(b.AsDouble());
        if (a.IsRat || b.IsRat)
        {
            // Exact cross-multiplication; both denominators are positive
            // (Rational canonical form, integers are num/1), so the sign of
            // the product comparison is the sign of the value comparison.
            var (an, ad) = a.AsRationalParts();
            var (bn, bd) = b.AsRationalParts();
            return (an * bd).CompareTo(bn * ad);
        }
        if (a.IsBig || b.IsBig)
            return a.AsBigInteger().CompareTo(b.AsBigInteger());
        return a.IntValue.CompareTo(b.IntValue);
    }
}
