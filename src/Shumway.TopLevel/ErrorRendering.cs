using Shumway.Core;

namespace Shumway.TopLevel;

/// <summary>
/// Pure helpers for turning an engine exception into the text a top level
/// shows. Free of console I/O, so both the REPL and the web front-end render
/// the same diagnostic — and so the formatting is unit-testable without
/// spinning up a session.
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
