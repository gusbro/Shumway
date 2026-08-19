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
            // SWI's random_float: a float in [0.0, 1.0). Host-dependent.
            case "random_float" when engine.Host is IRandomHost rh:
                return new Number(rh.Random.NextDouble());
        }
        // ISO §7.1.2 / §7.8.7: any other atom in arithmetic position raises
        // type_error(evaluable, Name/0) — the culprit is the INDICATOR
        // compound, not the bare atom (a catcher for foo/0 must unify).
        throw EvaluableTypeError(engine, name ?? "?", 0);
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

        // The FUNCTOR is checked before the arguments are evaluated:
        // `foo(bar)` is type_error(evaluable, foo/1), not bar/0 (§7.1.2 —
        // the outermost non-evaluable is the culprit; GNU and SWI agree).
        if ((arity == 1 && !TryUnOp(name, out _)
                && name is not ("msb" or "lsb" or "popcount" or "random"))
            || (arity == 2 && !TryBinOp(name, out _)))
            throw EvaluableTypeError(engine, name, arity);

        return arity switch
        {
            1 => EvaluateUnary(engine, name, engine.GetHeap(functorIdx + 1)),
            2 => EvaluateBinary(engine, name,
                                engine.GetHeap(functorIdx + 1),
                                engine.GetHeap(functorIdx + 2)),
            // Any other arity in arithmetic position is a non-evaluable
            // compound: ISO type_error(evaluable, Name/Arity).
            _ => throw EvaluableTypeError(engine, name, arity),
        };
    }

    /// <summary>The ISO ball for an unknown evaluable: the culprit is the
    /// procedure-INDICATOR <c>Name/Arity</c> (§9.x general error cases),
    /// built on the heap so the catcher's second slot binds the compound —
    /// not a bare atom, not a fresh variable.</summary>
    private static PrologRuntimeException EvaluableTypeError(
        Activation engine, string name, int arity)
    {
        int slashFid = FunctorTable.Intern(
            AtomTable.Intern("/", permanent: true).Id, 2);
        int b = engine.AllocateHeap(3);
        engine.SetHeap(b, Cell.Functor(slashFid));
        engine.SetHeap(b + 1, Cell.Atom(AtomTable.Intern(name, permanent: true).Id));
        engine.SetHeap(b + 2, Cell.Int(arity));
        return new PrologRuntimeException("type_error", "evaluable", engine, Cell.Str(b));
    }

    /// <summary>Evaluates <c>name(arg)</c> for a unary arithmetic function.
    /// Public so callers that already hold the function name and operand cell
    /// (e.g. <see cref="Evaluate"/> dispatching a compound) reuse the full ISO
    /// semantics.</summary>
    public static Number EvaluateUnary(Activation engine, string name, Cell argCell)
    {
        Number a = Evaluate(engine, argCell);
        if (TryUnOp(name, out UnOp op)) return ApplyUn(op, a);
        // SWI's random(IntExpr): a random integer in [0, IntExpr). Host-dependent,
        // so it is not a pure UnOp; drawn from the engine's seedable generator.
        if (name == "random" && engine.Host is IRandomHost rh && a.IsInt)
        {
            long n = a.IntValue;
            if (n <= 0) throw new PrologRuntimeException("evaluation_error", "undefined");
            return new Number((long)(rh.Random.NextDouble() * n));
        }
        // SWI msb/lsb: index of the most/least significant set bit of a
        // positive integer (varnumbers' power-of-two rounding uses msb).
        if ((name == "msb" || name == "lsb") && !a.IsRat)
        {
            if (a.IsFloat) throw IntTypeError(a);
            if (a.IsBig)
            {
                System.Numerics.BigInteger bv = a.BigValue;
                if (bv.Sign <= 0) throw EvalError("undefined");
                if (name == "msb") return new Number(bv.GetBitLength() - 1);
                // lsb: first non-zero byte of the little-endian magnitude.
                byte[] bytes = bv.ToByteArray();
                for (int i = 0; i < bytes.Length; i++)
                    if (bytes[i] != 0)
                        return new Number(i * 8L
                            + System.Numerics.BitOperations.TrailingZeroCount((uint)bytes[i]));
                throw EvalError("undefined");
            }
            long v = a.IntValue;
            if (v <= 0) throw EvalError("undefined");
            return new Number(name == "msb"
                ? 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)v)
                : System.Numerics.BitOperations.TrailingZeroCount((ulong)v));
        }
        // SWI/ECLiPSe popcount(Int): set-bit count of a non-negative integer.
        if (name == "popcount") return Popcount(a);
        // Unknown unary arithmetic function — ISO type_error(evaluable, Name/1).
        throw EvaluableTypeError(engine, name, 1);
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
        throw EvaluableTypeError(engine, name, 2);
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
        LogBase,       // log(Base, X) — Cor.2 two-argument logarithm
    }

    public enum UnOp : byte
    {
        Neg, Pos, Abs, Sign, BitNot, Sqrt, Sin, Cos, Tan, Asin, Acos, Atan,
        Exp, Log, Ceiling, Floor, Round, Truncate, Float, FloatIntPart,
        FloatFracPart, Integer,
        // Append only.
        Numerator, Denominator, Rationalize,
        Sinh, Cosh, Tanh, Asinh, Acosh, Atanh, Log10,
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
            case "log": op = BinOp.LogBase; return true;
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
            // ISO Cor.2 hyperbolic family.
            case "sinh": op = UnOp.Sinh; return true;
            case "cosh": op = UnOp.Cosh; return true;
            case "tanh": op = UnOp.Tanh; return true;
            case "asinh": op = UnOp.Asinh; return true;
            case "acosh": op = UnOp.Acosh; return true;
            case "atanh": op = UnOp.Atanh; return true;
            case "log10": op = UnOp.Log10; return true;
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
        BinOp.Pow => Power(a, b, preferRationals),
        BinOp.BitAnd => BitwiseAnd(a, b),
        BinOp.BitOr => BitwiseOr(a, b),
        BinOp.Xor => BitwiseXor(a, b),
        BinOp.Shl => ShiftLeft(a, b),
        BinOp.Shr => ShiftRight(a, b),
        BinOp.Gcd => Gcd(a, b),
        BinOp.Atan2 => FloatOperand(a) == 0 && b.AsDouble() == 0
            ? throw EvalError("undefined")
            : new Number(Math.Atan2(FloatOperand(a), b.AsDouble())),
        BinOp.IntDivFloor => FloorDivide(a, b),
        BinOp.PowFloat => PowFloatChecked(a, b),
        BinOp.Rdiv => RationalDivide(a, b),
        BinOp.LogBase => LogBase(a, b),
        _ => throw new PrologRuntimeException("type_error", "evaluable"),
    };

    private static PrologRuntimeException EvalError(string what)
        => new("evaluation_error", what);

    /// <summary>The operand of a float-domain function as a double: an
    /// unbounded integer whose magnitude exceeds the float range cannot be
    /// represented, so `sqrt(7^7^7)` is evaluation_error(float_overflow)
    /// (§9.1.4.1) rather than a silent infinity.</summary>
    private static double FloatOperand(Number a)
    {
        double d = a.AsDouble();
        if (!a.IsFloat && double.IsInfinity(d)) throw EvalError("float_overflow");
        return d;
    }

    /// <summary>A float result computed from FINITE inputs: infinity means
    /// the exact value fell outside the float range —
    /// <c>evaluation_error(float_overflow)</c> (§9.1.4.1); NaN means the
    /// function was applied outside its domain.</summary>
    private static Number FiniteOrOverflow(double r)
    {
        if (double.IsInfinity(r)) throw EvalError("float_overflow");
        if (double.IsNaN(r)) throw EvalError("undefined");
        return new Number(r);
    }

    /// <summary>Float→integer conversion (truncate / round / floor /
    /// ceiling / integer): a non-finite argument is undefined, and a value
    /// beyond the long range converts EXACTLY through BigInteger — the
    /// bare (long) cast silently produced long.MinValue garbage for
    /// <c>truncate(1.0e30)</c>.</summary>
    private static Number FloatToInteger(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) throw EvalError("undefined");
        if (d >= -9.2233720368547758e18 && d <= 9.2233720368547758e18)
        {
            long l = (long)d;
            // The boundary itself is inexact in double; re-check round-trip.
            if ((double)l == d || Math.Abs(d) < 9.2e18) return new Number(l);
        }
        return new Number(new BigInteger(d));
    }

    /// <summary>ISO <c>(**)/2</c> float power, §9.3.1.3: a zero base with a
    /// negative exponent divides by zero; a negative base demands an
    /// integral exponent (a fractional one is complex — undefined); a
    /// finite-input infinity is float_overflow.</summary>
    private static Number PowFloatChecked(Number a, Number b)
    {
        double x = FloatOperand(a), y = b.AsDouble();
        if (x == 0 && y < 0) throw EvalError("zero_divisor");
        if (x < 0 && y != Math.Floor(y)) throw EvalError("undefined");
        return FiniteOrOverflow(Math.Pow(x, y));
    }

    /// <summary>ISO <c>(div)/2</c> — integer division rounding toward
    /// negative infinity (pairs with <c>mod</c> the way <c>//</c> pairs
    /// with <c>rem</c>).</summary>
    private static Number FloorDivide(Number a, Number b)
    {
        EnsureBothInt(a, b);
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
        // ISO §9.3 domain conditions: outside a function's mathematical
        // domain is evaluation_error(undefined), never the IEEE inf/nan the
        // hardware would hand back (log(0), sqrt(-1), asin(2), …).
        UnOp.Sqrt => FloatOperand(a) < 0
            ? throw EvalError("undefined")
            : new Number(Math.Sqrt(FloatOperand(a))),
        UnOp.Sin => new Number(Math.Sin(FloatOperand(a))),
        UnOp.Cos => new Number(Math.Cos(FloatOperand(a))),
        UnOp.Tan => new Number(Math.Tan(FloatOperand(a))),
        UnOp.Asin => Math.Abs(FloatOperand(a)) > 1
            ? throw EvalError("undefined")
            : new Number(Math.Asin(FloatOperand(a))),
        UnOp.Acos => Math.Abs(FloatOperand(a)) > 1
            ? throw EvalError("undefined")
            : new Number(Math.Acos(FloatOperand(a))),
        UnOp.Atan => new Number(Math.Atan(FloatOperand(a))),
        // A finite argument whose exact result exceeds the float range is
        // evaluation_error(float_overflow) (§9.1.4.1), not silent infinity.
        UnOp.Exp => FiniteOrOverflow(Math.Exp(FloatOperand(a))),
        UnOp.Log => FloatOperand(a) <= 0
            ? throw EvalError("undefined")
            : new Number(Math.Log(FloatOperand(a))),
        UnOp.Log10 => FloatOperand(a) <= 0
            ? throw EvalError("undefined")
            : new Number(Math.Log10(FloatOperand(a))),
        UnOp.Ceiling => FloatToInteger(Math.Ceiling(FloatOperand(a))),
        UnOp.Floor => FloatToInteger(Math.Floor(FloatOperand(a))),
        // ISO 9.1.6.1 defines round(x) as floor(x + 1/2) — halves go toward
        // +inf (round(-3.5) is -3), NOT away from zero.
        UnOp.Round => FloatToInteger(Math.Floor(FloatOperand(a) + 0.5)),
        UnOp.Truncate => FloatToInteger(Math.Truncate(FloatOperand(a))),
        UnOp.Float => new Number(FloatOperand(a)),
        UnOp.FloatIntPart => new Number(Math.Truncate(FloatOperand(a))),
        UnOp.FloatFracPart => new Number(FloatOperand(a) - Math.Truncate(FloatOperand(a))),
        UnOp.Integer => a.IsFloat ? FloatToInteger(Math.Truncate(FloatOperand(a))) : a,
        UnOp.Numerator => Numerator(a),
        UnOp.Denominator => Denominator(a),
        UnOp.Rationalize => Rationalize(a),
        // ISO Cor.2 hyperbolics, same domain discipline: acosh below 1 and
        // atanh at or beyond ±1 are undefined; sinh/cosh overflow.
        UnOp.Sinh => FiniteOrOverflow(Math.Sinh(FloatOperand(a))),
        UnOp.Cosh => FiniteOrOverflow(Math.Cosh(FloatOperand(a))),
        UnOp.Tanh => new Number(Math.Tanh(FloatOperand(a))),
        UnOp.Asinh => new Number(Math.Asinh(FloatOperand(a))),
        UnOp.Acosh => FloatOperand(a) < 1
            ? throw EvalError("undefined")
            : new Number(Math.Acosh(FloatOperand(a))),
        UnOp.Atanh => Math.Abs(FloatOperand(a)) >= 1
            ? throw EvalError("undefined")
            : new Number(Math.Atanh(FloatOperand(a))),
        _ => throw new PrologRuntimeException("type_error", "evaluable"),
    };

    /// <summary>Exact rational division <c>A rdiv B</c> (ADR-039). Both operands
    /// must be integers or rationals; a float is a type error (rationals are
    /// exact). Produces a reduced rational, collapsing to an integer when
    /// exact.</summary>
    private static Number RationalDivide(Number a, Number b)
    {
        EnsureBothInt(a, b);
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

    /// <summary>log(Base, X): Cor.2 two-argument logarithm, always a float.
    /// Non-positive operand → undefined; base 1 → zero_divisor (the
    /// denominator ln(Base) vanishes).</summary>
    private static Number LogBase(Number a, Number b)
    {
        double bas = FloatOperand(a), x = b.AsDouble();
        if (bas <= 0 || x <= 0) throw EvalError("undefined");
        double den = Math.Log(bas);
        if (den == 0) throw EvalError("zero_divisor");
        return FiniteOrOverflow(Math.Log(x) / den);
    }

    private static Number Popcount(Number a)
    {
        if (a.IsFloat) throw IntTypeError(a);
        if (a.IsBig)
        {
            if (a.BigValue.Sign < 0)
                throw new PrologRuntimeException(
                    "domain_error", "not_less_than_zero", (object)a.BigValue);
            // Byte-wise count: BigInteger.PopCount needs .NET 7, and the value
            // is non-negative so the two's-complement bytes are the magnitude.
            long bits = 0;
            foreach (byte by in a.BigValue.ToByteArray())
                bits += System.Numerics.BitOperations.PopCount(by);
            return new Number(bits);
        }
        if (a.IntValue < 0)
            throw new PrologRuntimeException(
                "domain_error", "not_less_than_zero", (object)a.IntValue);
        return new Number((long)System.Numerics.BitOperations.PopCount((ulong)a.IntValue));
    }

    private static Number BitwiseNot(Number a)
    {
        if (a.IsFloat) throw IntTypeError(a);
        if (a.IsBig) return new Number(~a.BigValue);
        return new Number(~a.IntValue);
    }

    private static Number BitwiseAnd(Number a, Number b)
    {
        EnsureBothInt(a, b);
        if (a.IsBig || b.IsBig) return new Number(a.AsBigInteger() & b.AsBigInteger());
        return new Number(a.IntValue & b.IntValue);
    }

    private static Number BitwiseOr(Number a, Number b)
    {
        EnsureBothInt(a, b);
        if (a.IsBig || b.IsBig) return new Number(a.AsBigInteger() | b.AsBigInteger());
        return new Number(a.IntValue | b.IntValue);
    }

    private static Number BitwiseXor(Number a, Number b)
    {
        EnsureBothInt(a, b);
        if (a.IsBig || b.IsBig) return new Number(a.AsBigInteger() ^ b.AsBigInteger());
        return new Number(a.IntValue ^ b.IntValue);
    }

    private static Number ShiftLeft(Number a, Number b)
    {
        EnsureBothInt(a, b);
        int shift = (int)b.IntValue;
        if (a.IsBig) return new Number(a.BigValue << shift);
        // `checked` does NOT cover shifts in C# (they wrap silently, and the
        // count is masked to 0..63 — `1L << 64` is 1). Unbounded-integer
        // semantics: take the long path only when shifting back round-trips;
        // else promote to BigInteger (ieee_754's `1 << 63` bit patterns).
        long v = a.IntValue;
        if (shift >= 0 && shift < 64)
        {
            long r = v << shift;
            if ((r >> shift) == v) return new Number(r);
        }
        return new Number((System.Numerics.BigInteger)v << shift);
    }

    private static Number ShiftRight(Number a, Number b)
    {
        EnsureBothInt(a, b);
        int shift = (int)b.IntValue;
        if (a.IsBig) return new Number(a.BigValue >> shift);
        return new Number(a.IntValue >> shift);
    }

    private static Number Gcd(Number a, Number b)
    {
        EnsureBothInt(a, b);
        return new Number(System.Numerics.BigInteger.GreatestCommonDivisor(
            a.AsBigInteger(), b.AsBigInteger()));
    }

    private static Number Power(Number a, Number b, bool preferRationals = false)
    {
        // ISO: if both operands are integers and exponent >= 0, the result is
        // integer; otherwise it's a float.
        if (!a.IsFloat && !b.IsFloat && b.IsInt && b.IntValue >= 0)
        {
            if (b.IntValue > int.MaxValue)
                throw new PrologRuntimeException("evaluation_error", "exponent_too_large");
            return new Number(System.Numerics.BigInteger.Pow(a.AsBigInteger(), (int)b.IntValue));
        }
        // ISO 9.3.10 (Cor.2): integer base, NEGATIVE integer exponent —
        // 0 has no inverse (undefined), ±1 stay integer, and anything else
        // has no integer value: type_error(float, Base). Under
        // prefer_rationals (ADR-039) the exact rational 1/Base^|N| exists.
        if (!a.IsFloat && !b.IsFloat && !a.IsRat && b.IsInt && b.IntValue < 0)
        {
            BigInteger baseInt = a.AsBigInteger();
            if (baseInt.IsZero) throw EvalError("undefined");
            if (baseInt.IsOne) return new Number(1L);
            if ((-baseInt).IsOne)
                return new Number((b.IntValue & 1) == 0 ? 1L : -1L);
            if (b.IntValue < -int.MaxValue)
                throw new PrologRuntimeException("evaluation_error", "exponent_too_large");
            if (preferRationals)
                return new Number(Rational.Create(
                    BigInteger.One, BigInteger.Pow(baseInt, (int)(-b.IntValue))));
            throw new PrologRuntimeException("type_error", "float",
                a.IsBig ? (object)a.BigValue : (object)a.IntValue);
        }
        return PowFloatChecked(a, b);
    }

    /// <summary>ISO <c>type_error(integer, Culprit)</c> for an integer-only
    /// function fed a float — the culprit is the offending VALUE, boxed
    /// (these helpers run without an Activation, shared with the compiled
    /// tier).</summary>
    private static PrologRuntimeException IntTypeError(Number offender)
        => new("type_error", "integer", (object)offender.FloatValue);

    private static void EnsureBothInt(Number a, Number b)
    {
        if (a.IsFloat) throw IntTypeError(a);
        if (b.IsFloat) throw IntTypeError(b);
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

    /// <summary>Float result of a binary op: infinity or NaN produced from
    /// FINITE operands is float_overflow / undefined (§9.1.4.1); an
    /// already-infinite operand propagates untouched.</summary>
    private static Number FloatChecked(double x, double y, double r)
        => double.IsFinite(r) || !double.IsFinite(x) || !double.IsFinite(y)
            ? new Number(r)
            : FiniteOrOverflow(r);

    private static Number Add(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat)
            return FloatChecked(FloatOperand(a), b.AsDouble(), FloatOperand(a) + b.AsDouble());
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
        if (a.IsFloat || b.IsFloat)
            return FloatChecked(FloatOperand(a), b.AsDouble(), FloatOperand(a) - b.AsDouble());
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
        if (a.IsFloat || b.IsFloat)
            return FloatChecked(FloatOperand(a), b.AsDouble(), FloatOperand(a) * b.AsDouble());
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
        return FloatChecked(FloatOperand(a), bv, FloatOperand(a) / bv);
    }

    private static Number IntegerDivide(Number a, Number b)
    {
        EnsureBothInt(a, b);
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
        EnsureBothInt(a, b);
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
        EnsureBothInt(a, b);
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
