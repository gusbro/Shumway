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
    public enum Kind : byte { Int, Big, Float }

    public Kind ValueKind { get; }
    public long IntValue { get; }
    public BigInteger BigValue { get; }
    public double FloatValue { get; }

    public bool IsFloat => ValueKind == Kind.Float;
    public bool IsBig => ValueKind == Kind.Big;
    public bool IsInt => ValueKind == Kind.Int;

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
    }

    public Number(double floatValue)
    {
        ValueKind = Kind.Float;
        IntValue = 0;
        BigValue = default;
        FloatValue = floatValue;
    }

    /// <summary>The value as a double — exact for integers within
    /// <see cref="long"/> precision; lossy for BigInteger results that
    /// don't round-trip through double.</summary>
    public double AsDouble() => ValueKind switch
    {
        Kind.Float => FloatValue,
        Kind.Big => (double)BigValue,
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
    /// floats are placed on the heap via <see cref="Engine.MakeFloat"/> and
    /// the returned cell is a REF to the header.</summary>
    public Cell ToCell(Engine engine) => ValueKind switch
    {
        Kind.Float => Cell.Ref(engine.MakeFloat(FloatValue)),
        Kind.Big => engine.MakeBigInt(BigValue),
        _ => Cell.Int(IntValue),
    };

    public bool Equals(Number other) => ValueKind == other.ValueKind && ValueKind switch
    {
        Kind.Float => FloatValue == other.FloatValue,
        Kind.Big => BigValue.Equals(other.BigValue),
        _ => IntValue == other.IntValue,
    };

    public override bool Equals(object? obj) => obj is Number n && Equals(n);
    public override int GetHashCode() => ValueKind switch
    {
        Kind.Float => FloatValue.GetHashCode(),
        Kind.Big => BigValue.GetHashCode(),
        _ => IntValue.GetHashCode(),
    };

    public override string ToString() => ValueKind switch
    {
        Kind.Float => FormatPrologFloat(FloatValue),
        Kind.Big => BigValue.ToString(CultureInfo.InvariantCulture),
        _ => IntValue.ToString(CultureInfo.InvariantCulture),
    };

    private static readonly char[] ExpChars = { 'e', 'E' };

    /// <summary>Formats a double as a round-trippable ISO Prolog float: the
    /// mantissa always carries a decimal point and the exponent uses a
    /// lowercase <c>e</c>. .NET's <c>"R"</c> format emits forms like
    /// <c>1E-05</c> (no point, uppercase E) and <c>1</c> (for 1.0) that
    /// Shumway's own lexer reads back as an integer + a variable, not a
    /// float — so <c>writeq</c>/<c>write_canonical</c> output containing a
    /// small/large or whole-valued float was not re-consultable (it broke
    /// Logtalk's generated scratch files: <c>1E-05</c> tokenised as
    /// <c>1</c>, <c>E</c>). This produces <c>1.0e-05</c> / <c>1.0</c>.</summary>
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
        if (exp.StartsWith('+')) exp = exp[1..];
        return mant + "e" + exp;
    }

    public static int Compare(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat)
            return a.AsDouble().CompareTo(b.AsDouble());
        if (a.IsBig || b.IsBig)
            return a.AsBigInteger().CompareTo(b.AsBigInteger());
        return a.IntValue.CompareTo(b.IntValue);
    }
}
