using System.Globalization;
using System.Numerics;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// The runtime value type used by the arithmetic evaluator: an integer (within
/// <see cref="long"/> range), a big integer as limbs (ADR-064: a view over a
/// heap block or over a result buffer), a product of such a view by a limb-sized
/// multiplier left uncomputed until something needs its limbs, a
/// <see cref="BigInteger"/> for the operations that bridge to the platform type,
/// a rational, or a double. Promotion is automatic: any <c>+</c> / <c>-</c> /
/// <c>*</c> overflowing long retries in limbs; any operand being a float floats
/// the whole expression.
/// </summary>
public readonly struct Number : IEquatable<Number>
{
    public enum Kind : byte { Int, Big, Float, Rat, Limbs, Scaled }

    public Kind ValueKind { get; }
    public long IntValue { get; }
    private readonly BigInteger _big;
    private readonly double _float;   // the Float kind's value
    /// <summary>The float itself, or the nearest double to an exact value
    /// (<see cref="AsDouble"/>), computed when asked.</summary>
    public double FloatValue => ValueKind == Kind.Float ? _float : AsDouble();
    public Rational RatValue { get; }

    // Kind.Limbs and Kind.Scaled: the magnitude as limb cells, little-endian and
    // normalised, in a heap block (pooled false) or in a buffer owned by this
    // value (pooled true: ApplyBin, ApplyRel and ToCell return it to
    // LimbArith's pool when they consume the value). A Scaled value is those
    // limbs times _scale, a nonzero limb-sized multiplier, left uncomputed so
    // that a sum of two products runs as one fused pass and a product that
    // ends an evaluation is written straight onto the heap.
    private readonly Cell[]? _words;
    private readonly int _start;
    private readonly int _count;
    private readonly bool _negative;
    private readonly bool _pooled;
    private readonly ulong _scale;

    /// <summary>The value as a <see cref="BigInteger"/> for the Big, Limbs and
    /// Scaled kinds; a limb value converts (linear) each time it is asked.</summary>
    public BigInteger BigValue => ValueKind switch
    {
        Kind.Limbs => Shumway.Core.Limbs.ToBigInteger(LimbSpan, _negative),
        Kind.Scaled => Shumway.Core.Limbs.ToBigInteger(LimbSpan, _negative) * _scale,
        _ => _big,
    };

    public bool IsFloat => ValueKind == Kind.Float;
    /// <summary>A big integer in any form: limbs, a scaled view or a <see cref="BigInteger"/>.</summary>
    public bool IsBig => ValueKind is Kind.Big or Kind.Limbs or Kind.Scaled;
    public bool IsLimbs => ValueKind == Kind.Limbs;
    public bool IsScaled => ValueKind == Kind.Scaled;
    /// <summary>A value the limb kernels take as it is: limbs, scaled or not.</summary>
    public bool HasLimbs => ValueKind is Kind.Limbs or Kind.Scaled;
    public bool IsInt => ValueKind == Kind.Int;
    public bool IsRat => ValueKind == Kind.Rat;

    /// <summary>The limbs of a Limbs or Scaled value (ADR-064), normalised; for a
    /// Scaled value the base the multiplier applies to.</summary>
    public ReadOnlySpan<Cell> LimbSpan => new(_words!, _start, _count);
    public bool LimbsNegative => _negative;
    /// <summary>The multiplier of a Scaled value, below 2^60 and nonzero.</summary>
    public ulong Scale => _scale;
    internal Cell[]? LimbWords => _words;
    internal bool OwnsBuffer => _pooled && ValueKind is Kind.Limbs or Kind.Scaled;

    /// <summary>An exact number viewed as a rational (num/den). Valid for the
    /// integer kinds and Rat — an integer is num/1. Throws for a float.</summary>
    public (BigInteger Num, BigInteger Den) AsRationalParts() => ValueKind switch
    {
        Kind.Rat => (RatValue.Num, RatValue.Den),
        Kind.Big or Kind.Limbs or Kind.Scaled => (BigValue, BigInteger.One),
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
            _big = asBig._big;
        }
        else
        {
            ValueKind = Kind.Rat;
            RatValue = rat;
        }
    }

    public Number(long intValue)
    {
        // Anything outside the 60-bit inline range is a big integer even
        // though it fits in a long — Cell.Int refuses to encode values that
        // don't fit in the 60-bit payload.
        if (intValue < Cell.MinInt60 || intValue > Cell.MaxInt60)
        {
            ValueKind = Kind.Big;
            _big = new BigInteger(intValue);
        }
        else
        {
            ValueKind = Kind.Int;
            IntValue = intValue;
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
        }
        else
        {
            ValueKind = Kind.Big;
            _big = bigValue;
        }
    }

    public Number(double floatValue)
    {
        ValueKind = Kind.Float;
        _float = floatValue;
    }

    /// <summary>A value in limbs (ADR-064). <paramref name="count"/> is
    /// normalised; a value in the 60-bit inline range collapses to Int, the
    /// canonical form, and the caller then owns the buffer again.</summary>
    internal Number(Cell[] words, int start, int count, bool negative, bool pooled)
    {
        if (Shumway.Core.Limbs.TryToLong(new ReadOnlySpan<Cell>(words, start, count), negative, out long v))
        {
            ValueKind = Kind.Int;
            IntValue = v;
        }
        else
        {
            ValueKind = Kind.Limbs;
            _words = words;
            _start = start;
            _count = count;
            _negative = negative;
            _pooled = pooled;
        }
    }

    /// <summary>The limbs of <paramref name="limbs"/> (a Limbs or Scaled value)
    /// times <paramref name="scale"/>, a nonzero multiplier below 2^60, left
    /// uncomputed. Takes over the buffer the base owns.</summary>
    internal Number(in Number limbs, ulong scale, bool negative)
    {
        ValueKind = Kind.Scaled;
        _words = limbs._words;
        _start = limbs._start;
        _count = limbs._count;
        _negative = negative;
        _pooled = limbs._pooled;
        _scale = scale;
    }

    /// <summary>The value of a <see cref="Tag.BigInt"/> cell as a view over its
    /// heap block: nothing is copied. Valid until the heap grows or a collection
    /// runs, which no evaluation does before its last pop.</summary>
    public static Number FromHeap(Activation engine, Cell cell)
    {
        int h = cell.AsHeapIndex;
        Cell header = engine._heap[h];
        return new Number(engine._heap, h + 1, Shumway.Core.Limbs.Count(header),
            Shumway.Core.Limbs.Negative(header), pooled: false);
    }

    /// <summary>The same limbs (and multiplier) with the sign flipped. A result
    /// that fits the inline range collapses, so the caller releases a pooled
    /// buffer the result no longer names.</summary>
    internal Number Negated()
        => ValueKind == Kind.Scaled
            ? new Number(in this, _scale, !_negative)
            : new Number(_words!, _start, _count, !_negative, _pooled);

    /// <summary>A copy that owns no buffer and names no heap cell: a limb value
    /// becomes a <see cref="BigInteger"/>. For a holder that outlives the
    /// evaluation, such as the flag store. Consumes a pooled value.</summary>
    public Number Detached()
    {
        if (!IsLimbs && !IsScaled) return this;
        var detached = new Number(BigValue);
        Release();
        return detached;
    }

    /// <summary>Returns a pooled buffer to the pool. Called by whoever consumes
    /// the value; a view over the heap has nothing to return.</summary>
    internal void Release()
    {
        if (OwnsBuffer) LimbArith.Return(_words!);
    }

    /// <summary>The value as a double: an exact value rounds to the nearest
    /// double, ties to even, whatever its representation.</summary>
    public double AsDouble() => ValueKind switch
    {
        Kind.Float => _float,
        Kind.Big or Kind.Limbs or Kind.Scaled => DoubleConversion.FromInteger(BigValue),
        Kind.Rat => RatValue.ToDouble(),
        _ => IntValue,
    };

    /// <summary>The value as a <see cref="BigInteger"/>. Throws when the
    /// number is a float — arithmetic that mixes ints and floats produces
    /// a float result, so callers asking for a BigInteger have already
    /// checked the integer-only path.</summary>
    public BigInteger AsBigInteger() => ValueKind switch
    {
        Kind.Big or Kind.Limbs or Kind.Scaled => BigValue,
        Kind.Int => new BigInteger(IntValue),
        _ => throw new InvalidOperationException("Number is a float; no BigInteger view."),
    };

    /// <summary>Materialises the number as a heap cell. Inline-range ints go
    /// in <see cref="Cell.Int"/>; big integers become a block on the heap
    /// (ADR-064) and the returned cell is a <see cref="Cell.BigInt"/>, a
    /// Scaled value computing its product straight into the block; floats
    /// are placed on the heap via <see cref="Activation.MakeFloat"/> and the
    /// returned cell is a REF to the header. Consumes the value: a pooled
    /// limb buffer goes back to the pool.</summary>
    public Cell ToCell(Activation engine)
    {
        switch (ValueKind)
        {
            case Kind.Float: return Cell.Ref(engine.MakeFloat(FloatValue));
            case Kind.Big: return engine.MakeBigInt(_big);
            case Kind.Limbs:
            {
                Cell c = engine.MakeBigInt(LimbSpan, _negative);
                Release();
                return c;
            }
            case Kind.Scaled:
            {
                Cell c = engine.MakeBigIntProduct(LimbSpan, _scale, _negative);
                Release();
                return c;
            }
            case Kind.Rat: return engine.MakeRational(RatValue);
            default: return Cell.Int(IntValue);
        }
    }

    public bool Equals(Number other)
    {
        if (IsBig && other.IsBig) return BigValue.Equals(other.BigValue);
        return ValueKind == other.ValueKind && ValueKind switch
        {
            Kind.Float => FloatValue == other.FloatValue,
            Kind.Rat => RatValue.Equals(other.RatValue),
            _ => IntValue == other.IntValue,
        };
    }

    public override bool Equals(object? obj) => obj is Number n && Equals(n);
    public override int GetHashCode() => ValueKind switch
    {
        Kind.Float => FloatValue.GetHashCode(),
        Kind.Big or Kind.Limbs or Kind.Scaled => BigValue.GetHashCode(),
        Kind.Rat => RatValue.GetHashCode(),
        _ => IntValue.GetHashCode(),
    };

    public override string ToString() => ValueKind switch
    {
        Kind.Float => FormatPrologFloat(FloatValue),
        Kind.Big or Kind.Limbs or Kind.Scaled => BigValue.ToString(CultureInfo.InvariantCulture),
        Kind.Rat => RatValue.ToString(),
        _ => IntValue.ToString(CultureInfo.InvariantCulture),
    };

    private static readonly char[] ExpChars = { 'e', 'E' };

    /// <summary>Formats a double as a round-trippable ISO Prolog float: the
    /// mantissa always carries a decimal point and the exponent uses a
    /// lowercase <c>e</c> with an explicit sign. .NET's <c>"R"</c> format
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
        if (a.IsLimbs || b.IsLimbs || a.IsScaled || b.IsScaled)
            return LimbArith.Compare(in a, in b);
        if (a.IsBig || b.IsBig)
            return a.AsBigInteger().CompareTo(b.AsBigInteger());
        return a.IntValue.CompareTo(b.IntValue);
    }
}
