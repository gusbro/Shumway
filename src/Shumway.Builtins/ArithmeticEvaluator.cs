using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Recursive arithmetic-expression evaluator. ISO Prolog arithmetic isn't
/// directly term-evaluating — the right-hand side of <c>is/2</c> is a term
/// that the builtin walks, recognising specific functor names (<c>+</c>,
/// <c>-</c>, <c>*</c>, …) as operations and integer/float cells as leaves.
/// Variables in arithmetic position must be bound to evaluable terms,
/// otherwise an "instantiation" error is raised.
///
/// <para>The evaluator is leaf-recursive over the heap. Integer / float
/// promotion follows ISO: any operand being a float floats the whole
/// expression. Integer overflow throws — BigInt promotion is a future
/// chunk. Division by zero, type errors, and instantiation errors all
/// surface as <see cref="InvalidOperationException"/> with a clear message;
/// a future chunk will translate them into Prolog error terms.</para>
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
            Tag.Float => new Number(Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex))),
            Tag.Str => EvaluateCompound(engine, cell),
            Tag.Ref => throw new InvalidOperationException(
                "Arithmetic expression contains an unbound variable."),
            Tag.Atom => EvaluateAtomConstant(cell),
            _ => throw new InvalidOperationException(
                $"Cell with tag {cell.Tag} is not a valid arithmetic expression."),
        };
    }

    private static Cell ResolveRef(Engine engine, Cell cell)
    {
        if (cell.Tag != Tag.Ref) return cell;
        int addr = engine.Deref(cell.AsHeapIndex);
        return engine.GetHeap(addr);
    }

    private static Number EvaluateAtomConstant(Cell atomCell)
    {
        // Some atoms (pi, e, max_tagged_integer, etc.) are arithmetic
        // constants in ISO. None are supported yet — adding them is a
        // tiny incremental change.
        var atom = AtomTable.GetById(atomCell.AsAtomId);
        string name = atom?.Name ?? "?";
        throw new InvalidOperationException(
            $"Atom '{name}' is not a recognised arithmetic constant.");
    }

    private static Number EvaluateCompound(Engine engine, Cell strCell)
    {
        int functorIdx = strCell.AsHeapIndex;
        int functorId = engine.GetHeap(functorIdx).AsFunctorId;
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        string name = AtomTable.GetById(atomId)?.Name
                      ?? throw new InvalidOperationException(
                             $"Functor {functorId} has no name.");

        return arity switch
        {
            1 => EvaluateUnary(engine, name, engine.GetHeap(functorIdx + 1)),
            2 => EvaluateBinary(engine, name,
                                engine.GetHeap(functorIdx + 1),
                                engine.GetHeap(functorIdx + 2)),
            _ => throw new InvalidOperationException(
                $"No arithmetic function '{name}/{arity}'."),
        };
    }

    private static Number EvaluateUnary(Engine engine, string name, Cell argCell)
    {
        Number a = Evaluate(engine, argCell);
        return name switch
        {
            "-" => Negate(a),
            "+" => a,
            "abs" => Abs(a),
            _ => throw new InvalidOperationException(
                $"No arithmetic function '{name}/1'."),
        };
    }

    private static Number EvaluateBinary(Engine engine, string name, Cell aCell, Cell bCell)
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
            _ => throw new InvalidOperationException(
                $"No arithmetic function '{name}/2'."),
        };
    }

    // ---------- Operations ----------

    private static Number Negate(Number a) =>
        a.IsFloat ? new Number(-a.FloatValue) : new Number(checked(-a.IntValue));

    private static Number Abs(Number a) =>
        a.IsFloat ? new Number(Math.Abs(a.FloatValue))
                  : new Number(a.IntValue < 0 ? checked(-a.IntValue) : a.IntValue);

    private static Number Add(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat) return new Number(a.AsDouble() + b.AsDouble());
        return new Number(checked(a.IntValue + b.IntValue));
    }

    private static Number Subtract(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat) return new Number(a.AsDouble() - b.AsDouble());
        return new Number(checked(a.IntValue - b.IntValue));
    }

    private static Number Multiply(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat) return new Number(a.AsDouble() * b.AsDouble());
        return new Number(checked(a.IntValue * b.IntValue));
    }

    private static Number Divide(Number a, Number b)
    {
        // ISO: '/' is always real division (yields a float when operands are
        // both ints and the result isn't exact; for simplicity we treat it
        // as float division throughout).
        double bv = b.AsDouble();
        if (bv == 0.0) throw new InvalidOperationException("Division by zero in /.");
        return new Number(a.AsDouble() / bv);
    }

    private static Number IntegerDivide(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat)
            throw new InvalidOperationException("// requires integer operands.");
        if (b.IntValue == 0) throw new InvalidOperationException("Division by zero in //.");
        // ISO: truncating integer division (towards zero).
        return new Number(a.IntValue / b.IntValue);
    }

    private static Number Modulo(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat)
            throw new InvalidOperationException("mod requires integer operands.");
        if (b.IntValue == 0) throw new InvalidOperationException("Division by zero in mod.");
        // ISO `mod`: result has the sign of the divisor.
        long r = a.IntValue % b.IntValue;
        if ((r != 0) && ((r ^ b.IntValue) < 0)) r += b.IntValue;
        return new Number(r);
    }

    private static Number Remainder(Number a, Number b)
    {
        if (a.IsFloat || b.IsFloat)
            throw new InvalidOperationException("rem requires integer operands.");
        if (b.IntValue == 0) throw new InvalidOperationException("Division by zero in rem.");
        // ISO `rem`: result has the sign of the dividend (C's % operator).
        return new Number(a.IntValue % b.IntValue);
    }

    private static int Compare(Number a, Number b) => Number.Compare(a, b);
}
