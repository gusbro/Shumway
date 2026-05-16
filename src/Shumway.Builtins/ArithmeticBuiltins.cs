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

    private static Number EvaluateA(Engine engine) =>
        ArithmeticEvaluator.Evaluate(engine, engine.GetRegister(0));

    private static Number EvaluateB(Engine engine) =>
        ArithmeticEvaluator.Evaluate(engine, engine.GetRegister(1));
}
