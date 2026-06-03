using System.Numerics;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Recursive arithmetic-expression evaluator. ISO Prolog arithmetic isn't
/// directly term-evaluating — the right-hand side of <c>is/2</c> is a term
/// that the builtin walks, recognising specific functor names (<c>+</c>,
/// <c>-</c>, <c>*</c>, …) as operations and integer / bigint / float cells
/// as leaves. Variables in arithmetic position must be bound to evaluable
/// terms, otherwise an <c>instantiation_error</c> is raised.
///
/// <para>The evaluator is leaf-recursive over the heap. Integer / float
/// promotion follows ISO: any operand being a float floats the whole
/// expression. <c>+</c> / <c>-</c> / <c>*</c> / unary <c>-</c> / <c>abs</c>
/// fall back to <see cref="BigInteger"/> on long overflow; the result
/// auto-collapses to inline range when it fits.</para>
/// </summary>
public static class ArithmeticEvaluator
{
    /// <summary>Reads the value at <paramref name="cell"/> (dereferencing if
    /// it's a REF) and computes its numeric value.</summary>
    public static Number Evaluate(Engine engine, Cell cell)
    {
        cell = ResolveRef(engine, cell);
        return cell.Tag switch
        {
            Tag.Int => new Number(cell.AsInt),
            Tag.BigInt => new Number(engine.AsBigInt(cell)),
            Tag.Float => new Number(Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex))),
            Tag.Str => EvaluateCompound(engine, cell),
            Tag.Ref => throw new PrologRuntimeException("instantiation_error"),
            Tag.Atom => EvaluateAtomConstant(engine, cell),
            // Anything else in arithmetic position is non-evaluable.
            // ISO §7.1.2: type_error(evaluable, _). Chunk 144 carries
            // the offending cell so the value slot binds to it.
            _ => throw new PrologRuntimeException("type_error", "evaluable", engine, cell),
        };
    }

    private static Cell ResolveRef(Engine engine, Cell cell)
    {
        if (cell.Tag != Tag.Ref) return cell;
        int addr = engine.Deref(cell.AsHeapIndex);
        return engine.GetHeap(addr);
    }

    private static Number EvaluateAtomConstant(Engine engine, Cell atomCell)
    {
        // ISO §7.1.2 / §7.8.7: an atom in arithmetic position that isn't
        // a recognised arithmetic constant raises type_error(evaluable,
        // Name/0). Shumway currently recognises no atom constants
        // (pi, e, max_tagged_integer, … all still to come), so every
        // bound atom hits this path.
        //
        // Chunk 144 carries the offending atom cell as the
        // exception's Value, so a catcher matching
        // `error(type_error(evaluable, V), _)` binds V to the atom.
        throw new PrologRuntimeException("type_error", "evaluable", engine, atomCell);
    }

    private static Number EvaluateCompound(Engine engine, Cell strCell)
    {
        int functorIdx = strCell.AsHeapIndex;
        int functorId = engine.GetHeap(functorIdx).AsFunctorId;
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        // A functor without a name in the AtomTable is an engine invariant
        // violation, not a user error — InvalidOperationException stays.
        string name = AtomTable.GetById(atomId)?.Name
                      ?? throw new InvalidOperationException(
                             $"Functor {functorId} has no name.");

        return arity switch
        {
            1 => EvaluateUnary(engine, name, engine.GetHeap(functorIdx + 1)),
            2 => EvaluateBinary(engine, name,
                                engine.GetHeap(functorIdx + 1),
                                engine.GetHeap(functorIdx + 2)),
            // Any other arity in arithmetic position is a non-evaluable
            // compound: ISO type_error(evaluable, Name/Arity).
            _ => throw new PrologRuntimeException("type_error", "evaluable"),
        };
    }

    /// <summary>Evaluates <c>name(arg)</c> for a unary arithmetic function.
    /// Public so the inlined-arithmetic builtin (<c>$arith1</c>) can reuse the
    /// full ISO semantics without rebuilding the expression term on the heap.</summary>
    public static Number EvaluateUnary(Engine engine, string name, Cell argCell)
    {
        Number a = Evaluate(engine, argCell);
        return name switch
        {
            "-" => Negate(a),
            "+" => a,
            "abs" => Abs(a),
            "sign" => Sign(a),
            "\\" => BitwiseNot(a),
            "sqrt" => new Number(Math.Sqrt(a.AsDouble())),
            "sin" => new Number(Math.Sin(a.AsDouble())),
            "cos" => new Number(Math.Cos(a.AsDouble())),
            "tan" => new Number(Math.Tan(a.AsDouble())),
            "asin" => new Number(Math.Asin(a.AsDouble())),
            "acos" => new Number(Math.Acos(a.AsDouble())),
            "atan" => new Number(Math.Atan(a.AsDouble())),
            "exp" => new Number(Math.Exp(a.AsDouble())),
            "log" => new Number(Math.Log(a.AsDouble())),
            "ceiling" => new Number((long)Math.Ceiling(a.AsDouble())),
            "floor" => new Number((long)Math.Floor(a.AsDouble())),
            "round" => new Number((long)Math.Round(a.AsDouble(), MidpointRounding.AwayFromZero)),
            "truncate" => new Number((long)Math.Truncate(a.AsDouble())),
            "float" => new Number(a.AsDouble()),
            "float_integer_part" => new Number(Math.Truncate(a.AsDouble())),
            "float_fractional_part" => new Number(a.AsDouble() - Math.Truncate(a.AsDouble())),
            "integer" => a.IsFloat ? new Number((long)Math.Truncate(a.AsDouble())) : a,
            // Unknown unary arithmetic function — ISO type_error(evaluable, Name/1).
            _ => throw new PrologRuntimeException("type_error", "evaluable"),
        };
    }

    /// <summary>Evaluates <c>name(a, b)</c> for a binary arithmetic function.
    /// Public so the inlined-arithmetic builtin (<c>$arith2</c>) can reuse the
    /// full ISO semantics without rebuilding the expression term on the heap.</summary>
    public static Number EvaluateBinary(Engine engine, string name, Cell aCell, Cell bCell)
    {
        Number a = Evaluate(engine, aCell);
        Number b = Evaluate(engine, bCell);
        return name switch
        {
            "+" => Add(a, b),
            "-" => Subtract(a, b),
            "*" => Multiply(a, b),
            "/" => Divide(a, b),
            "//" => IntegerDivide(a, b),
            "mod" => Modulo(a, b),
            "rem" => Remainder(a, b),
            "min" => Compare(a, b) <= 0 ? a : b,
            "max" => Compare(a, b) >= 0 ? a : b,
            "**" or "^" => Power(a, b),
            "/\\" => BitwiseAnd(a, b),
            "\\/" => BitwiseOr(a, b),
            "xor" => BitwiseXor(a, b),
            "<<" => ShiftLeft(a, b),
            ">>" => ShiftRight(a, b),
            "gcd" => Gcd(a, b),
            "atan2" => new Number(Math.Atan2(a.AsDouble(), b.AsDouble())),
            // Unknown binary arithmetic function — ISO type_error(evaluable, Name/2).
            _ => throw new PrologRuntimeException("type_error", "evaluable"),
        };
    }

    private static Number Sign(Number a)
    {
        if (a.IsFloat) return new Number((double)Math.Sign(a.FloatValue));
        if (a.IsBig) return new Number(a.BigValue.Sign);
        return new Number((long)Math.Sign(a.IntValue));
    }

    private static Number BitwiseNot(Number a)
    {
        if (a.IsFloat)
            throw new PrologRuntimeException("type_error", "integer");
        if (a.IsBig) return new Number(~a.BigValue);
        return new Number(~a.IntValue);
    }

    private static Number BitwiseAnd(Number a, Number b)
    {
        EnsureBothInt(a, b, "/\\");
        if (a.IsBig || b.IsBig) return new Number(a.AsBigInteger() & b.AsBigInteger());
        return new Number(a.IntValue & b.IntValue);
    }

    private static Number BitwiseOr(Number a, Number b)
    {
        EnsureBothInt(a, b, "\\/");
        if (a.IsBig || b.IsBig) return new Number(a.AsBigInteger() | b.AsBigInteger());
        return new Number(a.IntValue | b.IntValue);
    }

    private static Number BitwiseXor(Number a, Number b)
    {
        EnsureBothInt(a, b, "xor");
        if (a.IsBig || b.IsBig) return new Number(a.AsBigInteger() ^ b.AsBigInteger());
        return new Number(a.IntValue ^ b.IntValue);
    }

    private static Number ShiftLeft(Number a, Number b)
    {
        EnsureBothInt(a, b, "<<");
        int shift = (int)b.IntValue;
        if (a.IsBig) return new Number(a.BigValue << shift);
        // long << large amounts overflows easily — promote to BigInteger.
        try { return new Number(checked(a.IntValue << shift)); }
        catch (OverflowException) { return new Number((System.Numerics.BigInteger)a.IntValue << shift); }
    }

    private static Number ShiftRight(Number a, Number b)
    {
        EnsureBothInt(a, b, ">>");
        int shift = (int)b.IntValue;
        if (a.IsBig) return new Number(a.BigValue >> shift);
        return new Number(a.IntValue >> shift);
    }

    private static Number Gcd(Number a, Number b)
    {
        EnsureBothInt(a, b, "gcd");
        return new Number(System.Numerics.BigInteger.GreatestCommonDivisor(
            a.AsBigInteger(), b.AsBigInteger()));
    }

    private static Number Power(Number a, Number b)
    {
        // ISO: if both operands are integers and exponent >= 0, the result is
        // integer; otherwise it's a float.
        if (!a.IsFloat && !b.IsFloat && b.IsInt && b.IntValue >= 0)
        {
            if (b.IntValue > int.MaxValue)
                throw new PrologRuntimeException("evaluation_error", "exponent_too_large");
            return new Number(System.Numerics.BigInteger.Pow(a.AsBigInteger(), (int)b.IntValue));
        }
        return new Number(Math.Pow(a.AsDouble(), b.AsDouble()));
    }

    private static void EnsureBothInt(Number a, Number b, string op)
    {
        if (a.IsFloat || b.IsFloat)
            throw new PrologRuntimeException("type_error",
                $"integer (left of {op})");
    }

    // ---------- Operations ----------

    private static Number Negate(Number a)
    {
        if (a.IsFloat) return new Number(-a.FloatValue);
        if (a.IsBig) return new Number(-a.BigValue);
        // long.MinValue: negation overflows long but fits in BigInteger.
        try { return new Number(checked(-a.IntValue)); }
        catch (OverflowException) { return new Number(-(BigInteger)a.IntValue); }
    }

    private static Number Abs(Number a)
    {
        if (a.IsFloat) return new Number(Math.Abs(a.FloatValue));
        if (a.IsBig) return new Number(BigInteger.Abs(a.BigValue));
        if (a.IntValue >= 0) return a;
        try { return new Number(checked(-a.IntValue)); }
        catch (OverflowException) { return new Number(-(BigInteger)a.IntValue); }
    }

    private static Number Add(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat) return new Number(a.AsDouble() + b.AsDouble());
        if (a.IsBig || b.IsBig) return new Number(a.AsBigInteger() + b.AsBigInteger());
        try { return new Number(checked(a.IntValue + b.IntValue)); }
        catch (OverflowException)
        {
            return new Number((BigInteger)a.IntValue + (BigInteger)b.IntValue);
        }
    }

    private static Number Subtract(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat) return new Number(a.AsDouble() - b.AsDouble());
        if (a.IsBig || b.IsBig) return new Number(a.AsBigInteger() - b.AsBigInteger());
        try { return new Number(checked(a.IntValue - b.IntValue)); }
        catch (OverflowException)
        {
            return new Number((BigInteger)a.IntValue - (BigInteger)b.IntValue);
        }
    }

    private static Number Multiply(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat) return new Number(a.AsDouble() * b.AsDouble());
        if (a.IsBig || b.IsBig) return new Number(a.AsBigInteger() * b.AsBigInteger());
        try { return new Number(checked(a.IntValue * b.IntValue)); }
        catch (OverflowException)
        {
            return new Number((BigInteger)a.IntValue * (BigInteger)b.IntValue);
        }
    }

    private static Number Divide(Number a, Number b)
    {
        // ISO: '/' is always real division (yields a float when operands are
        // both ints and the result isn't exact; for simplicity we treat it
        // as float division throughout).
        double bv = b.AsDouble();
        if (bv == 0.0) throw new PrologRuntimeException("evaluation_error", "zero_divisor");
        return new Number(a.AsDouble() / bv);
    }

    private static Number IntegerDivide(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat)
            throw new PrologRuntimeException("type_error", "integer");
        if (a.IsBig || b.IsBig)
        {
            BigInteger bb = b.AsBigInteger();
            if (bb.IsZero) throw new PrologRuntimeException("evaluation_error", "zero_divisor");
            return new Number(a.AsBigInteger() / bb);
        }
        if (b.IntValue == 0) throw new PrologRuntimeException("evaluation_error", "zero_divisor");
        // ISO: truncating integer division (towards zero).
        return new Number(a.IntValue / b.IntValue);
    }

    private static Number Modulo(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat)
            throw new PrologRuntimeException("type_error", "integer");
        if (a.IsBig || b.IsBig)
        {
            BigInteger bigA = a.AsBigInteger();
            BigInteger bigB = b.AsBigInteger();
            if (bigB.IsZero) throw new PrologRuntimeException("evaluation_error", "zero_divisor");
            BigInteger bigR = bigA % bigB;
            if (!bigR.IsZero && bigR.Sign != bigB.Sign) bigR += bigB;
            return new Number(bigR);
        }
        if (b.IntValue == 0) throw new PrologRuntimeException("evaluation_error", "zero_divisor");
        // ISO `mod`: result has the sign of the divisor.
        long r = a.IntValue % b.IntValue;
        if ((r != 0) && ((r ^ b.IntValue) < 0)) r += b.IntValue;
        return new Number(r);
    }

    private static Number Remainder(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat)
            throw new PrologRuntimeException("type_error", "integer");
        if (a.IsBig || b.IsBig)
        {
            BigInteger bigB = b.AsBigInteger();
            if (bigB.IsZero) throw new PrologRuntimeException("evaluation_error", "zero_divisor");
            return new Number(a.AsBigInteger() % bigB);
        }
        if (b.IntValue == 0) throw new PrologRuntimeException("evaluation_error", "zero_divisor");
        // ISO `rem`: result has the sign of the dividend (C's % operator).
        return new Number(a.IntValue % b.IntValue);
    }

    private static int Compare(Number a, Number b) => Number.Compare(a, b);
}
