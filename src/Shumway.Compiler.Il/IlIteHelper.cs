using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// ADR-025 stage (b) — runtime support for the inline if-then-else /
/// disjunction shape in Tier-1 IL.
///
/// <para>The WAM lowering (<c>ClauseCompiler.CompileInlineIte</c>) emits a
/// mid-body <c>try_me_else ELSE (arity 0)</c>. Under IL the equivalent choice
/// point is pushed with <see cref="Resume"/> as its callback and the
/// <b>resume marker</b> of the ELSE cursor stored in the CP's cursor slot: on
/// backtrack the engine invokes the callback, which parks the marker as the
/// PC (<see cref="Activation.ResumeAtReturnPc"/>) so the dispatch loop decodes it
/// and re-enters the owning predicate's delegate at the ELSE label — the same
/// resume protocol chunk-218 backtrackable builtins use. Must be public: a
/// persisted-bundle .dll references the field from a fresh process.</para>
/// </summary>
public static class IlIteHelper
{
    /// <summary>Choice-point callback for the inline-ITE CP. The second
    /// parameter is the CP's cursor SLOT, which for this shape carries the
    /// encoded resume marker of the ELSE branch (patched name-relative in
    /// persisted bundles). Always succeeds — there is always an else branch
    /// to run.</summary>
    public static readonly System.Func<Activation, int, bool> Resume =
        static (engine, marker) =>
        {
            engine.ResumeAtReturnPc(marker);
            return true;
        };
}
