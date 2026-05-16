using System.Globalization;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// The runtime value type used by the arithmetic evaluator: an integer (which
/// fits in the inline 60-bit signed range — BigInt promotion is not yet
/// implemented) or a double. Arithmetic operations promote the result to
/// double whenever either operand is a float, matching ISO Prolog semantics
/// for the standard <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c> family.
/// </summary>
public readonly struct Number : IEquatable<Number>
{
    public bool IsFloat { get; }
    public long IntValue { get; }
    public double FloatValue { get; }

    public Number(long intValue)
    {
        IsFloat = false;
        IntValue = intValue;
        FloatValue = intValue;
    }

    public Number(double floatValue)
    {
        IsFloat = true;
        IntValue = 0;
        FloatValue = floatValue;
    }

    /// <summary>The value as a double — exact for integers within
    /// <see cref="long"/> precision.</summary>
    public double AsDouble() => IsFloat ? FloatValue : (double)IntValue;

    /// <summary>Materialises the number as a heap cell. Integers go in inline
    /// <see cref="Cell.Int"/> cells; floats are placed on the heap via
    /// <see cref="Engine.MakeFloat"/> and the returned cell is a REF to the
    /// header (the binding policy from <c>BindVarToValue</c>).</summary>
    public Cell ToCell(Engine engine) =>
        IsFloat ? Cell.Ref(engine.MakeFloat(FloatValue)) : Cell.Int(IntValue);

    public bool Equals(Number other) =>
        IsFloat == other.IsFloat
        && (IsFloat ? FloatValue == other.FloatValue : IntValue == other.IntValue);

    public override bool Equals(object? obj) => obj is Number n && Equals(n);
    public override int GetHashCode() => IsFloat ? FloatValue.GetHashCode() : IntValue.GetHashCode();

    public override string ToString() =>
        IsFloat ? FloatValue.ToString("R", CultureInfo.InvariantCulture)
                : IntValue.ToString(CultureInfo.InvariantCulture);

    public static int Compare(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat)
            return a.AsDouble().CompareTo(b.AsDouble());
        return a.IntValue.CompareTo(b.IntValue);
    }
}
