namespace Shumway.Compiler.Parsing;

/// <summary>
/// The seven ISO Prolog operator forms. Each describes how an operator combines with
/// its operand(s) and how its precedence relates to its arguments' allowed precedences.
///
/// <para>The convention: <c>x</c> means an argument must have a strictly lower
/// precedence than the operator; <c>y</c> means it may have equal-or-lower
/// precedence. So <c>yfx</c> (left-associative infix) lets the LEFT operand have
/// the same precedence as the operator (so chains group left-to-right), while
/// requiring the RIGHT operand to be strictly lower.</para>
/// </summary>
public enum OperatorType
{
    /// <summary>Prefix, operand strictly lower precedence (<c>x</c>).</summary>
    Fx,
    /// <summary>Prefix, operand same or lower precedence (<c>y</c>).</summary>
    Fy,
    /// <summary>Postfix, operand strictly lower.</summary>
    Xf,
    /// <summary>Postfix, operand same or lower.</summary>
    Yf,
    /// <summary>Infix, both operands strictly lower (non-associative).</summary>
    Xfx,
    /// <summary>Infix, left strictly lower, right same or lower
    /// (right-associative).</summary>
    Xfy,
    /// <summary>Infix, left same or lower, right strictly lower
    /// (left-associative).</summary>
    Yfx,
}

internal static class OperatorTypeExtensions
{
    public static bool IsPrefix(this OperatorType t) => t is OperatorType.Fx or OperatorType.Fy;
    public static bool IsPostfix(this OperatorType t) => t is OperatorType.Xf or OperatorType.Yf;
    public static bool IsInfix(this OperatorType t) => t is OperatorType.Xfx or OperatorType.Xfy or OperatorType.Yfx;
}
