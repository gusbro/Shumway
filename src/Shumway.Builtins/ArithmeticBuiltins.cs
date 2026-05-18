using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// The ISO arithmetic predicates: <c>is/2</c> for assignment and the six
/// comparison operators (<c>=:=</c>, <c>=\=</c>, <c>&lt;</c>, <c>&gt;</c>,
/// <c>=&lt;</c>, <c>&gt;=</c>) that take two arithmetic expressions. The
/// expressions themselves are evaluated by <see cref="ArithmeticEvaluator"/>.
/// </summary>
public static class ArithmeticBuiltins
{
    /// <summary><c>X is Expr</c> — evaluates <c>Expr</c> and unifies the
    /// result with <c>X</c>. The result is either an Int cell (integer
    /// result) or a heap-allocated Float (when promotion was needed).</summary>
    public static bool Is(Engine engine)
    {
        Number result = ArithmeticEvaluator.Evaluate(engine, engine.GetRegister(1));
        Cell cell = result.ToCell(engine);
        return engine.UnifyRegisterWithCell(0, cell);
    }

    public static bool ArithEqual(Engine engine) =>
        Number.Compare(EvaluateA(engine), EvaluateB(engine)) == 0;

    public static bool ArithNotEqual(Engine engine) =>
        Number.Compare(EvaluateA(engine), EvaluateB(engine)) != 0;

    public static bool ArithLess(Engine engine) =>
        Number.Compare(EvaluateA(engine), EvaluateB(engine)) < 0;

    public static bool ArithGreater(Engine engine) =>
        Number.Compare(EvaluateA(engine), EvaluateB(engine)) > 0;

    public static bool ArithLessOrEqual(Engine engine) =>
        Number.Compare(EvaluateA(engine), EvaluateB(engine)) <= 0;

    public static bool ArithGreaterOrEqual(Engine engine) =>
        Number.Compare(EvaluateA(engine), EvaluateB(engine)) >= 0;

    /// <summary><c>between(Low, High, X)</c> — integer range. Phase-1
    /// scope: <c>Low</c> and <c>High</c> must be ground integers; <c>X</c>
    /// is either ground (in which case we check <c>Low ≤ X ≤ High</c>) or
    /// unbound (in which case <c>X</c> binds to <c>Low</c>). Multi-solution
    /// enumeration of every integer in the range lands when call/N gets a
    /// runtime choice-point integration.</summary>
    public static bool Between(Engine engine)
    {
        Cell lo = Resolve(engine, engine.GetRegister(0));
        Cell hi = Resolve(engine, engine.GetRegister(1));
        Cell x = Resolve(engine, engine.GetRegister(2));
        if (lo.Tag != Tag.Int || hi.Tag != Tag.Int)
            throw new InvalidOperationException(
                "between/3: Low and High must be ground integers in Phase 1.");
        long loVal = lo.AsInt;
        long hiVal = hi.AsInt;
        if (loVal > hiVal) return false;

        if (x.Tag == Tag.Int)
        {
            long xVal = x.AsInt;
            return xVal >= loVal && xVal <= hiVal;
        }
        if (x.Tag == Tag.Ref)
        {
            // Bind X to Low (first solution).
            return engine.UnifyRegisterWithCell(2, Cell.Int(loVal));
        }
        return false;
    }

    /// <summary><c>succ(X, Y)</c> — successor of a non-negative integer.
    /// Either <c>X</c> or <c>Y</c> must be ground. With <c>X</c> ground
    /// (non-negative) the result is <c>Y = X + 1</c>; with <c>Y</c> ground
    /// (positive) the result is <c>X = Y - 1</c>. Negative inputs raise
    /// <c>InvalidOperationException</c>.</summary>
    public static bool Succ(Engine engine)
    {
        Cell xc = Resolve(engine, engine.GetRegister(0));
        Cell yc = Resolve(engine, engine.GetRegister(1));

        if (xc.Tag == Tag.Int)
        {
            long xv = xc.AsInt;
            if (xv < 0)
                throw new InvalidOperationException(
                    $"succ/2: first argument must be non-negative, got {xv}.");
            return engine.UnifyRegisterWithCell(1, Cell.Int(xv + 1));
        }
        if (yc.Tag == Tag.Int)
        {
            long yv = yc.AsInt;
            if (yv <= 0) return false;   // succ(_, 0) has no solution
            return engine.UnifyRegisterWithCell(0, Cell.Int(yv - 1));
        }
        throw new InvalidOperationException(
            "succ/2: at least one of X, Y must be sufficiently instantiated.");
    }

    /// <summary><c>plus(X, Y, Z)</c> — integer addition relation with
    /// any one of the three arguments allowed to be free. With X+Y
    /// bound it computes Z; with X+Z bound it computes Y = Z-X; with
    /// Y+Z bound it computes X = Z-Y. (Chunk 54.)</summary>
    public static bool Plus(Engine engine)
    {
        Cell xc = Resolve(engine, engine.GetRegister(0));
        Cell yc = Resolve(engine, engine.GetRegister(1));
        Cell zc = Resolve(engine, engine.GetRegister(2));

        bool xBound = xc.Tag == Tag.Int;
        bool yBound = yc.Tag == Tag.Int;
        bool zBound = zc.Tag == Tag.Int;
        int boundCount = (xBound ? 1 : 0) + (yBound ? 1 : 0) + (zBound ? 1 : 0);
        if (boundCount < 2)
            throw new PrologRuntimeException("instantiation_error");

        if (xBound && yBound)
        {
            long sum = checked(xc.AsInt + yc.AsInt);
            return engine.UnifyRegisterWithCell(2, Cell.Int(sum));
        }
        if (xBound && zBound)
        {
            long y = checked(zc.AsInt - xc.AsInt);
            return engine.UnifyRegisterWithCell(1, Cell.Int(y));
        }
        // yBound && zBound
        {
            long x = checked(zc.AsInt - yc.AsInt);
            return engine.UnifyRegisterWithCell(0, Cell.Int(x));
        }
    }

    private static Number EvaluateA(Engine engine) =>
        ArithmeticEvaluator.Evaluate(engine, engine.GetRegister(0));

    private static Number EvaluateB(Engine engine) =>
        ArithmeticEvaluator.Evaluate(engine, engine.GetRegister(1));

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
