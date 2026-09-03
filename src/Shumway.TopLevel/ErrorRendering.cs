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
                ShumwayPrologException pex =>
                    $"error: {AstTermRenderer.RenderQuoted(pex.Term)}",
                PrologRuntimeException re => $"error: {FormatRuntimeError(re)}",
                // A query whose TEXT is perfect syntax naming an
                // unrepresentable value (a float above max_float): the ISO
                // error shape, though the goal never ran so nothing catches it.
                Shumway.Compiler.Parsing.ParseException { RepresentationFlaw: { } flaw } =>
                    $"error: representation_error({flaw})",
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

    /// <summary>Formats a <see cref="PrologRuntimeException"/> by rendering
    /// the SAME ISO ball term catch/3 would unify with (issue #65 pinned
    /// the drift: the message said <c>existence_error(inex/0)</c> while the
    /// ball carried <c>existence_error(procedure, inex/0)</c>, and a float
    /// culprit printed as the C# default <c>0</c> instead of <c>0.0</c>).
    /// Quoted rendering, so the culprit is re-readable text. The offending
    /// builtin's <c>Name/Arity</c> follows as context — unless it is a
    /// <c>$</c>-named internal helper, which is engine machinery, not a
    /// predicate the user called (the same rule the stack frames follow).
    ///
    /// <para>A syntax_error's detail is a reader MESSAGE with positions,
    /// not a term; it keeps its plain formatting.</para></summary>
    public static string FormatRuntimeError(PrologRuntimeException re)
    {
        ArgumentNullException.ThrowIfNull(re);
        string body;
        if (re.Kind == "syntax_error")
            body = $"syntax_error({re.Detail})";
        else
            body = MetaBuiltins.TranslateRuntimeError(re)
                    is Shumway.Compiler.Ast.CompoundTerm { Functor: "error", Args.Length: 2 } ball
                ? AstTermRenderer.RenderQuoted(ball.Args[0])
                : string.IsNullOrEmpty(re.Detail) ? re.Kind : $"{re.Kind}({re.Detail})";
        if (!string.IsNullOrEmpty(re.BuiltinName) && re.BuiltinName[0] != '$')
            return $"{body} in {re.BuiltinName}/{re.BuiltinArity}";
        return body;
    }
}
