namespace Shumway.Builtins;

/// <summary>
/// Render-time options for <see cref="TermRenderer"/>. Mirrors the ISO
/// <c>write_term/2</c> options:
/// <list type="bullet">
/// <item><see cref="Quoted"/> — atoms that aren't a plain alphanumeric
///   identifier or a known symbolic operator get single-quoted, and
///   strings escape their internal quotes.</item>
/// <item><see cref="IgnoreOps"/> — render every compound in the
///   canonical <c>name(arg, ...)</c> form, never as an infix or prefix
///   operator. Today this is a placeholder switch — the renderer
///   already uses the canonical form everywhere — but the option is
///   accepted so user code can pass it without surprise.</item>
/// <item><see cref="Numbervars"/> — compound terms with functor
///   <c>'$VAR'(N)</c> render as letter-suffixed variable names
///   (<c>A</c>, <c>B</c>, …, <c>Z</c>, <c>A1</c>, <c>B1</c>, …).
///   Matches what <c>numbervars/3</c> in the engine produces.</item>
/// </list>
/// </summary>
public sealed class TermRenderOptions
{
    public static TermRenderOptions Default { get; } = new TermRenderOptions();

    public bool Quoted { get; set; } = false;
    public bool IgnoreOps { get; set; } = false;
    public bool Numbervars { get; set; } = false;
}
