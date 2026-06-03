using System.Collections.Generic;
using Shumway.Compiler.Ast;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compile-time arithmetic inlining (Phase 25). Rewrites a body goal
/// <c>X is Expr</c>, where <c>Expr</c> is a recognised arithmetic expression,
/// into a flat sequence of <c>'$arith2'(T, Op, A, B)</c> / <c>'$arith1'(T, Op,
/// A)</c> builtin goals — so the expression term is never built on the heap.
///
/// <para>Each operand of a <c>$arith</c> goal is a register value (a variable,
/// an integer/float literal, or a synthetic variable holding a previously
/// computed sub-expression), never a structure. A nested sub-expression is
/// hoisted into a fresh synthetic variable (named <c>$G&lt;n&gt;</c>, which can
/// never collide with a source variable since Prolog variables cannot start
/// with <c>$</c>) and computed by its own <c>$arith</c> goal first, in
/// left-to-right order so any evaluation error surfaces exactly where
/// <c>is/2</c> would raise it.</para>
///
/// <para>This runs on the clause compiler's working goal list only — the
/// stored clause AST is untouched, so <c>clause/2</c> and <c>listing</c> still
/// show the original <c>X is Expr</c>. An expression containing any functor the
/// evaluator does not recognise (as a 1- or 2-arity node) is left as a plain
/// <c>is/2</c> goal, which reproduces the same <c>type_error(evaluable, _)</c>.
/// Only top-level conjunctive <c>is/2</c> goals are rewritten; an <c>is/2</c>
/// nested inside a control construct that the compiler leaves as a runtime
/// term is left for the normal path.</para>
/// </summary>
internal static class ArithInline
{
    // Must mirror ArithmeticEvaluator.EvaluateBinary / EvaluateUnary exactly:
    // a functor inlined here but not handled there would change an
    // (evaluator) type_error into a wrong success, and vice-versa.
    private static readonly HashSet<string> BinOps = new()
    {
        "+", "-", "*", "/", "//", "mod", "rem", "min", "max", "**", "^",
        "/\\", "\\/", "xor", "<<", ">>", "gcd", "atan2",
    };

    private static readonly HashSet<string> UnOps = new()
    {
        "-", "+", "abs", "sign", "\\", "sqrt", "sin", "cos", "tan",
        "asin", "acos", "atan", "exp", "log", "ceiling", "floor", "round",
        "truncate", "float", "float_integer_part", "float_fractional_part",
        "integer",
    };

    /// <summary>Returns a goal list with every inlinable top-level
    /// <c>X is Expr</c> rewritten. Returns the input list unchanged (same
    /// reference) when nothing is inlinable.</summary>
    public static List<Term> Expand(List<Term> goals)
    {
        List<Term>? result = null;
        int counter = 0;
        for (int i = 0; i < goals.Count; i++)
        {
            Term g = goals[i];
            if (g is CompoundTerm { Functor: "is", Args: { Length: 2 } } isGoal
                && IsInlinable(isGoal.Args[1]))
            {
                result ??= new List<Term>(goals.GetRange(0, i));
                FlattenInto(isGoal.Args[0], isGoal.Args[1], result, ref counter);
            }
            else
            {
                result?.Add(g);
            }
        }
        return result ?? goals;
    }

    /// <summary>True iff <paramref name="expr"/> is a compound whose every
    /// compound node is a recognised 1- or 2-arity arithmetic op (leaves —
    /// vars / numbers / atoms — are always fine; the builtin reproduces
    /// <c>is/2</c>'s handling of them, including errors).</summary>
    private static bool IsInlinable(Term expr)
        => expr is CompoundTerm && IsInlinableNode(expr);

    private static bool IsInlinableNode(Term t)
    {
        if (t is not CompoundTerm c) return true;   // leaf
        bool known = (c.Args.Length == 2 && BinOps.Contains(c.Functor))
                  || (c.Args.Length == 1 && UnOps.Contains(c.Functor));
        if (!known) return false;
        foreach (Term a in c.Args)
            if (!IsInlinableNode(a)) return false;
        return true;
    }

    // Appends the goals computing `expr` into `target`. Sub-expressions are
    // emitted first (left-to-right), so evaluation order matches is/2.
    private static void FlattenInto(Term target, Term expr, List<Term> output, ref int counter)
    {
        var c = (CompoundTerm)expr;   // guaranteed compound by IsInlinable
        if (c.Args.Length == 2)
        {
            Term a = OperandOf(c.Args[0], output, ref counter);
            Term b = OperandOf(c.Args[1], output, ref counter);
            output.Add(new CompoundTerm("$arith2",
                new[] { target, new AtomTerm(c.Functor), a, b }));
        }
        else
        {
            Term a = OperandOf(c.Args[0], output, ref counter);
            output.Add(new CompoundTerm("$arith1",
                new[] { target, new AtomTerm(c.Functor), a }));
        }
    }

    // A leaf operand passes through; a sub-expression compound is hoisted into
    // a fresh synthetic variable computed by its own preceding $arith goal.
    private static Term OperandOf(Term operand, List<Term> output, ref int counter)
    {
        if (operand is CompoundTerm)
        {
            var synth = new VarTerm("$G" + counter++);
            FlattenInto(synth, operand, output, ref counter);
            return synth;
        }
        return operand;
    }
}
