using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// ADR-018 — the runtime evaluation stack for the arithmetic instruction set
/// (<c>a_eval_push</c> / <c>a_eval_bin</c> / <c>a_eval_un</c> / <c>a_eval_is</c>
/// / <c>a_eval_cmp</c>). A postfix arithmetic sequence pushes operands and
/// applies operators against this <see cref="Number"/> stack, leaving the WAM
/// heap untouched — no synthetic variables, no expression term.
///
/// <para>Both execution tiers route through these static methods so there is a
/// single implementation: the Tier-0 bytecode interpreter dispatches the
/// <c>a_eval_*</c> opcodes here, and the Tier-1 IL emit (<c>IlPredicateCompiler</c>)
/// emits direct calls to them. The Tier-1 path is the reason the stack lives
/// here rather than as an instance field on the interpreter: an IL delegate
/// only carries the <see cref="Engine"/> argument, so the scratch must be
/// reachable through a static.</para>
///
/// <para><b>Thread-safety / engine-agility.</b> The backing array is
/// <c>[ThreadStatic]</c>, but it is <i>not</i> engine state — it carries no
/// engine identity and is always fully drained within a single arithmetic
/// evaluation. Arithmetic is leaf: no Prolog goal executes between the first
/// <c>a_eval_push</c> and the terminating <c>a_eval_is</c>/<c>a_eval_cmp</c>,
/// so evaluations never nest or interleave on one thread, and the stack is
/// empty between goals. The engine therefore stays thread-agile (none of
/// <i>its</i> state is thread-static) — this is transient per-thread scratch,
/// the textbook case the invariant's "no [ThreadStatic] for engine state"
/// rule is not about.</para>
/// </summary>
public static class ArithEvalStack
{
    [ThreadStatic] private static Number[]? _stack;
    [ThreadStatic] private static int _top;

    private static Number[] Ensure() => _stack ??= new Number[32];

    /// <summary>Pushes an already-evaluated number (used by the interpreter
    /// for resolved bigint / float literal operands).</summary>
    public static void Push(Number n)
    {
        Number[] s = Ensure();
        if (_top == s.Length)
        {
            System.Array.Resize(ref s, s.Length * 2);
            _stack = s;
        }
        s[_top++] = n;
    }

    /// <summary>Pushes a 32-bit integer literal operand (a_eval_push kind 0).</summary>
    public static void PushInt(long value) => Push(new Number(value));

    /// <summary>Evaluates the X-register and pushes the result (a_eval_push kind 3).</summary>
    public static void PushReg(Engine engine, int reg)
    {
        try { Push(ArithmeticEvaluator.Evaluate(engine, engine.GetRegister(reg))); }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
    }

    /// <summary>Evaluates the permanent (Y) slot and pushes the result
    /// (a_eval_push kind 4).</summary>
    public static void PushY(Engine engine, int slot)
    {
        try { Push(ArithmeticEvaluator.Evaluate(engine, engine.GetY(slot))); }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
    }

    /// <summary>Applies a binary operator to the top two stack entries
    /// (a_eval_bin), leaving the result on top.</summary>
    public static void Bin(int op)
    {
        Number[] s = _stack!;
        Number b = s[--_top];
        try { s[_top - 1] = ArithmeticEvaluator.ApplyBin((ArithmeticEvaluator.BinOp)op, s[_top - 1], b); }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
    }

    /// <summary>Applies a unary operator to the top stack entry (a_eval_un).</summary>
    public static void Un(int op)
    {
        Number[] s = _stack!;
        try { s[_top - 1] = ArithmeticEvaluator.ApplyUn((ArithmeticEvaluator.UnOp)op, s[_top - 1]); }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
    }

    /// <summary>Pops the result and unifies it with the X-register
    /// (a_eval_is kind 3). Returns the unification outcome.</summary>
    public static bool IsReg(Engine engine, int reg)
    {
        Cell cell;
        try { cell = _stack![--_top].ToCell(engine); }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
        return engine.UnifyRegisterWithCell(reg, cell);
    }

    /// <summary>Pops the result and unifies it with the permanent (Y) slot
    /// (a_eval_is kind 4). Returns the unification outcome.</summary>
    public static bool IsPerm(Engine engine, int slot)
    {
        Cell cell;
        try { cell = _stack![--_top].ToCell(engine); }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
        return engine.UnifyPermanentWithCell(slot, cell);
    }

    /// <summary>Pops the top two entries and applies an arithmetic comparison
    /// (a_eval_cmp). Returns whether the relation holds.</summary>
    public static bool Cmp(int rel)
    {
        Number[] s = _stack!;
        Number b = s[--_top];
        Number a = s[--_top];
        return ArithmeticEvaluator.ApplyRel((ArithmeticEvaluator.RelOp)rel, a, b);
    }
}
