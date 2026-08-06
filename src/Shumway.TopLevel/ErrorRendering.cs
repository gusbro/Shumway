using Shumway.Core;
using Shumway.Embedding;

namespace Shumway.TopLevel;

/// <summary>
/// Pure helpers for turning an engine exception into the text a top level
/// shows. Free of console I/O, so both the REPL and the web front-end render
/// the same diagnostic — and so the formatting is unit-testable without
/// spinning up a session.
/// </summary>
public static class ErrorRendering
{
    /// <summary>The whole diagnostic a top level shows for a failed goal: the
    /// error itself, then the engine's call stack with source positions where it
    /// has them. Returns the lines unprefixed — a console puts <c>%</c> in front,
    /// a web UI styles them — so the two agree on WHAT is reported and differ
    /// only in how it looks.
    ///
    /// <para>Frames whose name starts with <c>$</c> are the engine's own
    /// machinery (meta-call helpers, launcher stubs) and are skipped: they are
    /// not code the user wrote. A frame with no real position prints without
    /// one rather than claiming line 1.</para></summary>
    public static IReadOnlyList<string> Describe(PrologEngine engine, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(ex);

        var lines = new List<string>
        {
            ex switch
            {
                ShumwayPrologException pex => $"error: {pex.Term}",
                PrologRuntimeException re => $"error: {FormatRuntimeError(re)}",
                _ => $"{ex.GetType().Name}: {ex.Message}",
            },
        };

        var trace = engine.LastErrorStackTraceWithPositions;
        if (trace is null) return lines;
        foreach (var f in trace)
        {
            if (f.Name.StartsWith('$')) continue;
            bool positionless =
                f.Position.Line <= 1 && f.Position.Column <= 1 && f.Position.Offset == 0;
            lines.Add(positionless
                ? $"  at {f.Name}/{f.Arity}"
                : $"  at {f.Name}/{f.Arity} ({f.Position})");
        }
        return lines;
    }

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
