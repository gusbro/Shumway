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
    public static Number Evaluate(Activation engine, Cell cell)
    {
        cell = ResolveRef(engine, cell);
        return cell.Tag switch
        {
            Tag.Int => new Number(cell.AsInt),
            Tag.BigInt => new Number(engine.AsBigInt(cell)),
            Tag.Rational => new Number(engine.AsRational(cell)),
            Tag.Float => new Number(Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex))),
            Tag.Str => EvaluateCompound(engine, cell),
            Tag.Ref => throw new PrologRuntimeException("instantiation_error"),
            Tag.Atom => EvaluateAtomConstant(engine, cell),
            // Anything else in arithmetic position is non-evaluable.
            // ISO §7.1.2: type_error(evaluable, _); the offending cell
            // travels in the exception so the value slot binds to it.
            _ => throw new PrologRuntimeException("type_error", "evaluable", engine, cell),
        };
    }

    private static Cell ResolveRef(Activation engine, Cell cell)
    {
        if (cell.Tag != Tag.Ref) return cell;
        int addr = engine.Deref(cell.AsHeapIndex);
        return engine.GetHeap(addr);
    }

    private static Number EvaluateAtomConstant(Activation engine, Cell atomCell)
    {
        // ISO §9: recognised arithmetic constants. `pi` is the one ISO
        // constant (GProlog's table marks e / epsilon as extensions; they
        // are cheap to accept alongside).
        string? name = AtomTable.GetById(atomCell.AsAtomId)?.Name;
        switch (name)
        {
            case "pi": return new Number(Math.PI);
            case "e": return new Number(Math.E);
            // Machine epsilon (2^-52): difference between 1.0 and the
            // smallest float > 1.0 — NOT .NET's double.Epsilon (the
            // smallest positive denormal).
            case "epsilon": return new Number(Math.Pow(2, -52));
        }
        // ISO §7.1.2 / §7.8.7: any other atom in arithmetic position raises
        // type_error(evaluable, Name/0). The offending atom cell travels as
        // the exception's Value, so a catcher matching
        // `error(type_error(evaluable, V), _)` binds V to the atom.
        throw new PrologRuntimeException("type_error", "evaluable", engine, atomCell);
    }

    private static Number EvaluateCompound(Activation engine, Cell strCell)
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
    /// Public so callers that already hold the function name and operand cell
    /// (e.g. <see cref="Evaluate"/> dispatching a compound) reuse the full ISO
    /// semantics.</summary>
    public static Number EvaluateUnary(Activation engine, string name, Cell argCell)
    {
        Number a = Evaluate(engine, argCell);
        if (TryUnOp(name, out UnOp op)) return ApplyUn(op, a);
        // Unknown unary arithmetic function — ISO type_error(evaluable, Name/1).
        throw new PrologRuntimeException("type_error", "evaluable");
    }

    /// <summary>Evaluates <c>name(a, b)</c> for a binary arithmetic function.
    /// Public so callers that already hold the function name and operand cells
    /// (e.g. <see cref="Evaluate"/> dispatching a compound) reuse the full ISO
    /// semantics.</summary>
    public static Number EvaluateBinary(Activation engine, string name, Cell aCell, Cell bCell)
    {
        Number a = Evaluate(engine, aCell);
        Number b = Evaluate(engine, bCell);
        if (TryBinOp(name, out BinOp op)) return ApplyBin(op, a, b, engine.PreferRationals);
        // Unknown binary arithmetic function — ISO type_error(evaluable, Name/2).
        throw new PrologRuntimeException("type_error", "evaluable");
    }

    // ---------- ADR-018: arithmetic instruction set op codes ----------
    //
    // The a_bin / a_un / a_cmp opcodes (ADR-018) carry a small op code — these
    // enums. The compiler maps a functor name to one (Try*); the interpreter
    // applies it over the eval stack (Apply*). There is ONE operation switch
    // per arity, shared by the name-based Evaluate* (the term-evaluation path,
    // for a variable bound to an unevaluated expression) and the opcode path.

    public enum BinOp : byte
    {
        Add, Sub, Mul, Div, IntDiv, Mod, Rem, Min, Max, Pow,
        BitAnd, BitOr, Xor, Shl, Shr, Gcd, Atan2,
        // Only append here — the numeric values are baked into bytecode.
        IntDivFloor,   // (div)/2 — integer division rounding toward -inf
        PowFloat,      // (**)/2 — ISO: result is ALWAYS a float (^ keeps IF)
        Rdiv,          // (rdiv)/2 — exact rational division (ADR-039)
    }

    public enum UnOp : byte
    {
        Neg, Pos, Abs, Sign, BitNot, Sqrt, Sin, Cos, Tan, Asin, Acos, Atan,
        Exp, Log, Ceiling, Floor, Round, Truncate, Float, FloatIntPart,
        FloatFracPart, Integer,
        // Append only.
        Numerator, Denominator, Rationalize,
    }

    public enum RelOp : byte { Eq, Neq, Lt, Gt, Le, Ge }

    /// <summary>Functor name → binary op code; false for a non-arithmetic name.</summary>
    public static bool TryBinOp(string name, out BinOp op)
    {
        switch (name)
        {
            case "+": op = BinOp.Add; return true;
            case "-": op = BinOp.Sub; return true;
            case "*": op = BinOp.Mul; return true;
            case "/": op = BinOp.Div; return true;
            case "//": op = BinOp.IntDiv; return true;
            case "div": op = BinOp.IntDivFloor; return true;
            case "mod": op = BinOp.Mod; return true;
            case "rem": op = BinOp.Rem; return true;
            case "min": op = BinOp.Min; return true;
            case "max": op = BinOp.Max; return true;
            // ISO 9.3.1 vs 9.3.10: `**` always yields a FLOAT; `^` yields an
            // integer for integer operands (integer power).
            case "**": op = BinOp.PowFloat; return true;
            case "^": op = BinOp.Pow; return true;
            case "/\\": op = BinOp.BitAnd; return true;
            case "\\/": op = BinOp.BitOr; return true;
            case "xor": op = BinOp.Xor; return true;
            case "<<": op = BinOp.Shl; return true;
            case ">>": op = BinOp.Shr; return true;
            case "gcd": op = BinOp.Gcd; return true;
            case "atan2": op = BinOp.Atan2; return true;
            case "rdiv": op = BinOp.Rdiv; return true;
            default: op = default; return false;
        }
    }

    /// <summary>Functor name → unary op code; false for a non-arithmetic name.</summary>
    public static bool TryUnOp(string name, out UnOp op)
    {
        switch (name)
        {
            case "-": op = UnOp.Neg; return true;
            case "+": op = UnOp.Pos; return true;
            case "abs": op = UnOp.Abs; return true;
            case "sign": op = UnOp.Sign; return true;
            case "\\": op = UnOp.BitNot; return true;
            case "sqrt": op = UnOp.Sqrt; return true;
            case "sin": op = UnOp.Sin; return true;
            case "cos": op = UnOp.Cos; return true;
            case "tan": op = UnOp.Tan; return true;
            case "asin": op = UnOp.Asin; return true;
            case "acos": op = UnOp.Acos; return true;
            case "atan": op = UnOp.Atan; return true;
            case "exp": op = UnOp.Exp; return true;
            case "log": op = UnOp.Log; return true;
            case "ceiling": op = UnOp.Ceiling; return true;
            case "floor": op = UnOp.Floor; return true;
            case "round": op = UnOp.Round; return true;
            case "truncate": op = UnOp.Truncate; return true;
            case "float": op = UnOp.Float; return true;
            case "float_integer_part": op = UnOp.FloatIntPart; return true;
            case "float_fractional_part": op = UnOp.FloatFracPart; return true;
            case "integer": op = UnOp.Integer; return true;
            case "numerator": op = UnOp.Numerator; return true;
            case "denominator": op = UnOp.Denominator; return true;
            case "rationalize": op = UnOp.Rationalize; return true;
            default: op = default; return false;
        }
    }

    /// <summary>Comparison functor name → relation op code.</summary>
    public static bool TryRelOp(string name, out RelOp op)
    {
        switch (name)
        {
            case "=:=": op = RelOp.Eq; return true;
            case "=\\=": op = RelOp.Neq; return true;
            case "<": op = RelOp.Lt; return true;
            case ">": op = RelOp.Gt; return true;
            case "=<": op = RelOp.Le; return true;
            case ">=": op = RelOp.Ge; return true;
            default: op = default; return false;
        }
    }

    /// <summary>Applies a binary op to two already-evaluated numbers.
    /// <paramref name="preferRationals"/> is the <c>prefer_rationals</c> flag,
    /// consulted only by <c>/</c> (ADR-039); the default false is the
    /// ISO / GProlog behaviour and the compile-time constant-folding path.</summary>
    public static Number ApplyBin(BinOp op, Number a, Number b, bool preferRationals = false) => op switch
    {
        BinOp.Add => Add(a, b),
        BinOp.Sub => Subtract(a, b),
        BinOp.Mul => Multiply(a, b),
        BinOp.Div => Divide(a, b, preferRationals),
        BinOp.IntDiv => IntegerDivide(a, b),
        BinOp.Mod => Modulo(a, b),
        BinOp.Rem => Remainder(a, b),
        BinOp.Min => Compare(a, b) <= 0 ? a : b,
        BinOp.Max => Compare(a, b) >= 0 ? a : b,
        BinOp.Pow => Power(a, b),
        BinOp.BitAnd => BitwiseAnd(a, b),
        BinOp.BitOr => BitwiseOr(a, b),
        BinOp.Xor => BitwiseXor(a, b),
        BinOp.Shl => ShiftLeft(a, b),
        BinOp.Shr => ShiftRight(a, b),
        BinOp.Gcd => Gcd(a, b),
        BinOp.Atan2 => new Number(Math.Atan2(a.AsDouble(), b.AsDouble())),
        BinOp.IntDivFloor => FloorDivide(a, b),
        BinOp.PowFloat => new Number(Math.Pow(a.AsDouble(), b.AsDouble())),
        BinOp.Rdiv => RationalDivide(a, b),
        _ => throw new PrologRuntimeException("type_error", "evaluable"),
    };

    /// <summary>ISO <c>(div)/2</c> — integer division rounding toward
    /// negative infinity (pairs with <c>mod</c> the way <c>//</c> pairs
    /// with <c>rem</c>).</summary>
    private static Number FloorDivide(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat)
            throw new PrologRuntimeException("type_error", "integer");
        if (a.IsBig || b.IsBig)
        {
            BigInteger bigA = a.AsBigInteger();
            BigInteger bigB = b.AsBigInteger();
            if (bigB.IsZero) throw new PrologRuntimeException("evaluation_error", "zero_divisor");
            BigInteger q = BigInteger.DivRem(bigA, bigB, out BigInteger r);
            if (!r.IsZero && r.Sign != bigB.Sign) q -= 1;
            return new Number(q);
        }
        if (b.IntValue == 0) throw new PrologRuntimeException("evaluation_error", "zero_divisor");
        long qi = a.IntValue / b.IntValue;
        long ri = a.IntValue % b.IntValue;
        if (ri != 0 && (ri < 0) != (b.IntValue < 0)) qi--;
        return new Number(qi);
    }

    /// <summary>Applies a unary op to an already-evaluated number.</summary>
    public static Number ApplyUn(UnOp op, Number a) => op switch
    {
        UnOp.Neg => Negate(a),
        UnOp.Pos => a,
        UnOp.Abs => Abs(a),
        UnOp.Sign => Sign(a),
        UnOp.BitNot => BitwiseNot(a),
        UnOp.Sqrt => new Number(Math.Sqrt(a.AsDouble())),
        UnOp.Sin => new Number(Math.Sin(a.AsDouble())),
        UnOp.Cos => new Number(Math.Cos(a.AsDouble())),
        UnOp.Tan => new Number(Math.Tan(a.AsDouble())),
        UnOp.Asin => new Number(Math.Asin(a.AsDouble())),
        UnOp.Acos => new Number(Math.Acos(a.AsDouble())),
        UnOp.Atan => new Number(Math.Atan(a.AsDouble())),
        UnOp.Exp => new Number(Math.Exp(a.AsDouble())),
        UnOp.Log => new Number(Math.Log(a.AsDouble())),
        UnOp.Ceiling => new Number((long)Math.Ceiling(a.AsDouble())),
        UnOp.Floor => new Number((long)Math.Floor(a.AsDouble())),
        UnOp.Round => new Number((long)Math.Round(a.AsDouble(), MidpointRounding.AwayFromZero)),
        UnOp.Truncate => new Number((long)Math.Truncate(a.AsDouble())),
        UnOp.Float => new Number(a.AsDouble()),
        UnOp.FloatIntPart => new Number(Math.Truncate(a.AsDouble())),
        UnOp.FloatFracPart => new Number(a.AsDouble() - Math.Truncate(a.AsDouble())),
        UnOp.Integer => a.IsFloat ? new Number((long)Math.Truncate(a.AsDouble())) : a,
        UnOp.Numerator => Numerator(a),
        UnOp.Denominator => Denominator(a),
        UnOp.Rationalize => Rationalize(a),
        _ => throw new PrologRuntimeException("type_error", "evaluable"),
    };

    /// <summary>Exact rational division <c>A rdiv B</c> (ADR-039). Both operands
    /// must be integers or rationals; a float is a type error (rationals are
    /// exact). Produces a reduced rational, collapsing to an integer when
    /// exact.</summary>
    private static Number RationalDivide(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat)
            throw new PrologRuntimeException("type_error", "integer");
        var (an, ad) = a.AsRationalParts();
        var (bn, bd) = b.AsRationalParts();
        // (an/ad) / (bn/bd) = (an*bd) / (ad*bn)
        return new Number(Rational.Create(an * bd, ad * bn));
    }

    private static Number Numerator(Number a)
    {
        if (a.IsRat) return new Number(a.RatValue.Num);
        if (a.IsInt || a.IsBig) return a;   // integer: numerator is itself
        throw new PrologRuntimeException("type_error", "rational");
    }

    private static Number Denominator(Number a)
    {
        if (a.IsRat) return new Number(a.RatValue.Den);
        if (a.IsInt || a.IsBig) return new Number(1L);   // integer: denominator 1
        throw new PrologRuntimeException("type_error", "rational");
    }

    /// <summary>ISO/SWI <c>rationalize/1</c>: the simplest rational that equals
    /// the argument. For an exact operand it is the value itself; a float is
    /// converted to the exact rational with the shortest decimal expansion that
    /// round-trips (here, the exact binary value reduced).</summary>
    private static Number Rationalize(Number a)
    {
        if (!a.IsFloat) return a;
        return new Number(FloatToRational(a.FloatValue));
    }

    /// <summary>Exact rational equal to a finite double (its precise binary
    /// value as a fraction).</summary>
    internal static Rational FloatToRational(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
            throw new PrologRuntimeException("evaluation_error", "undefined");
        if (d == 0.0) return Rational.Create(BigInteger.Zero, BigInteger.One);
        long bits = BitConverter.DoubleToInt64Bits(d);
        bool negative = bits < 0;
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent++;            // subnormal
        else mantissa |= 0x10000000000000L;       // implicit leading 1
        exponent -= 1075;                          // bias + mantissa width
        BigInteger num = mantissa;
        if (negative) num = -num;
        return exponent >= 0
            ? Rational.Create(num * BigInteger.Pow(2, exponent), BigInteger.One)
            : Rational.Create(num, BigInteger.Pow(2, -exponent));
    }

    /// <summary>Compares two already-evaluated numbers under a relation.</summary>
    public static bool ApplyRel(RelOp rel, Number a, Number b)
    {
        int c = Compare(a, b);
        return rel switch
        {
            RelOp.Eq => c == 0,
            RelOp.Neq => c != 0,
            RelOp.Lt => c < 0,
            RelOp.Gt => c > 0,
            RelOp.Le => c <= 0,
            RelOp.Ge => c >= 0,
            _ => false,
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
        if (a.IsRat) return new Number(Rational.Create(-a.RatValue.Num, a.RatValue.Den));
        if (a.IsBig) return new Number(-a.BigValue);
        // long.MinValue: negation overflows long but fits in BigInteger.
        try { return new Number(checked(-a.IntValue)); }
        catch (OverflowException) { return new Number(-(BigInteger)a.IntValue); }
    }

    private static Number Abs(Number a)
    {
        if (a.IsFloat) return new Number(Math.Abs(a.FloatValue));
        if (a.IsRat) return new Number(Rational.Create(BigInteger.Abs(a.RatValue.Num), a.RatValue.Den));
        if (a.IsBig) return new Number(BigInteger.Abs(a.BigValue));
        if (a.IntValue >= 0) return a;
        try { return new Number(checked(-a.IntValue)); }
        catch (OverflowException) { return new Number(-(BigInteger)a.IntValue); }
    }

    private static Number Add(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat) return new Number(a.AsDouble() + b.AsDouble());
        if (a.IsRat || b.IsRat)
        {
            var (an, ad) = a.AsRationalParts();
            var (bn, bd) = b.AsRationalParts();
            return new Number(Rational.Create(an * bd + bn * ad, ad * bd));
        }
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
        if (a.IsRat || b.IsRat)
        {
            var (an, ad) = a.AsRationalParts();
            var (bn, bd) = b.AsRationalParts();
            return new Number(Rational.Create(an * bd - bn * ad, ad * bd));
        }
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
        if (a.IsRat || b.IsRat)
        {
            var (an, ad) = a.AsRationalParts();
            var (bn, bd) = b.AsRationalParts();
            return new Number(Rational.Create(an * bn, ad * bd));
        }
        if (a.IsBig || b.IsBig) return new Number(a.AsBigInteger() * b.AsBigInteger());
        try { return new Number(checked(a.IntValue * b.IntValue)); }
        catch (OverflowException)
        {
            return new Number((BigInteger)a.IntValue * (BigInteger)b.IntValue);
        }
    }

    private static Number Divide(Number a, Number b, bool preferRationals)
    {
        // A rational operand makes '/' exact regardless of the flag (it is
        // already in the exact domain); two integers under the
        // `prefer_rationals` flag also produce an exact rational when the
        // quotient isn't integral. Otherwise '/' is float division (ISO /
        // GProlog default).
        if (!a.IsFloat && !b.IsFloat && (a.IsRat || b.IsRat || preferRationals))
        {
            var (an, ad) = a.AsRationalParts();
            var (bn, bd) = b.AsRationalParts();
            if ((bn * ad).IsZero) throw new PrologRuntimeException("evaluation_error", "zero_divisor");
            return new Number(Rational.Create(an * bd, ad * bn));
        }
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
