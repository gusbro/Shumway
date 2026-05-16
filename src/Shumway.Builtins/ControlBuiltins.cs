using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// The trivial control predicates: <c>fail/0</c> always reports failure
/// (triggering backtrack-or-fail), <c>true/0</c> always succeeds.
///
/// <para><c>true/0</c> is rarely emitted as a runtime call because the
/// compiler's <c>FlattenConjunction</c> drops <c>true</c> goals during AST
/// rewriting. It's registered anyway so a meta-level dispatch (if it ever
/// reaches a literal <c>true</c>) does the right thing.</para>
///
/// <para><c>fail/0</c> is essential for the compile-time expansion of
/// negation-as-failure: <c>\+ G</c> rewrites to a helper whose body ends in
/// <c>!, fail</c>.</para>
/// </summary>
public static class ControlBuiltins
{
    public static bool Fail(Engine engine) => false;
    public static bool True(Engine engine) => true;
}
