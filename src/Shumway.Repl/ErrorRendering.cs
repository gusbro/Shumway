using Shumway.Core;

namespace Shumway.Repl;

/// <summary>
/// Pure helpers the REPL's
/// <c>ReplTopLevel.PrintError</c> calls into. Factored out so the
/// formatting logic is unit-testable without spinning up a real
/// REPL session.
/// </summary>
public static class ErrorRendering
{
    /// <summary>Formats a <see cref="PrologRuntimeException"/> into the
    /// ISO-shaped <c>kind(detail)</c> string, plus the offending
    /// builtin's <c>Name/Arity</c> as the error context when the
    /// interpreter stamped it.
    ///
    /// <para>Examples:</para>
    /// <list type="bullet">
    /// <item><c>{ Kind: "evaluation_error", Detail: "zero_divisor", BuiltinName: "is", BuiltinArity: 2 }</c>
    ///   → <c>"evaluation_error(zero_divisor) in is/2"</c></item>
    /// <item><c>{ Kind: "instantiation_error", Detail: "" }</c>
    ///   → <c>"instantiation_error"</c></item>
    /// </list></summary>
    public static string FormatRuntimeError(PrologRuntimeException re)
    {
        ArgumentNullException.ThrowIfNull(re);
        string body = string.IsNullOrEmpty(re.Detail)
            ? re.Kind
            : $"{re.Kind}({re.Detail})";
        if (!string.IsNullOrEmpty(re.BuiltinName))
            return $"{body} in {re.BuiltinName}/{re.BuiltinArity}";
        return body;
    }
}
