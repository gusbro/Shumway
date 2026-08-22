using Shumway.Core;

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
///   operator.</item>
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

    /// <summary><c>portray_text(true)</c>: a list of characters or of printable
    /// codes renders as <c>"…"</c> instead of element by element. Off by
    /// default — the ISO form is the list. The decision is made on the list's
    /// CONTENT, never on how it is stored (ADR-047 decision 7), so a packed
    /// list and the cons list it denotes print identically.</summary>
    public bool PortrayText { get; set; } = false;

    /// <summary><c>max_depth(N)</c>: compounds/list tails nested deeper
    /// than N render as <c>...</c> / <c>|...</c>. 0 = unlimited.</summary>
    public int MaxDepth { get; set; }

    /// <summary>Mutable render-time nesting counter for
    /// <see cref="MaxDepth"/>. Only touched when MaxDepth &gt; 0, so the
    /// shared Default instance stays immutable in practice.</summary>
    public int CurrentDepth { get; set; }

    /// <summary>Cycle safety for the writer (rational trees): the heap
    /// addresses of the compound nodes currently being rendered — the path
    /// from the root. A revisit is a back-edge; the first is followed (one
    /// unroll, matching how much of the cycle other systems show) and any
    /// further one renders <c>...</c>. Reset by the entry wrapper; lazy, so
    /// an acyclic term allocates nothing.</summary>
    internal System.Collections.Generic.HashSet<int>? OnPath;
    internal int CycleUnrollBudget = 1;

    /// <summary><c>portrayed(true)</c>: called for every subterm before
    /// default rendering; returning true means the hook produced the
    /// output (SICStus portray/1 protocol — the embedding wires it to a
    /// re-entrant call of the user's portray/1).</summary>
    public Func<Activation, Cell, System.IO.TextWriter, bool>? Portray { get; set; }

    /// <summary>The <c>variable_names(Bindings)</c> write_term option
    /// (SWI / SICStus): a map from a variable's dereferenced heap index to
    /// the source name it should print as, instead of the default
    /// <c>_Gn</c>. Built from a <c>[Name=Var, ...]</c> list at call time;
    /// only entries whose <c>Var</c> is still unbound are recorded.
    /// <c>null</c> when the option is absent.</summary>
    public System.Collections.Generic.Dictionary<int, string>? VariableNames { get; set; }

    /// <summary>Operator-lookup view used by the renderer to decide
    /// whether a compound's functor should print in operator form
    /// (<c>a + b</c>) instead of canonical form (<c>+(a, b)</c>). When
    /// <c>null</c> or <see cref="IgnoreOps"/> is true, the renderer
    /// always emits canonical form.</summary>
    public IOperatorLookup? Operators { get; set; }

    /// <summary>When <c>true</c> (the default), infix/postfix operators
    /// built entirely from ISO graphic characters (<c>/</c>, <c>+</c>,
    /// <c>=..</c>, …) render with no surrounding spaces — <c>hola/2</c>,
    /// <c>1+2*3</c> — matching the SWI / GNU / SICStus convention.
    /// Alphabetic operators (<c>is</c>, <c>mod</c>) always keep their
    /// spaces so the tokens stay separable, and prefix operators keep a
    /// trailing space to avoid fusing with a numeric / symbolic argument
    /// (<c>- 1</c> vs the literal <c>-1</c>). Set to <c>false</c> for the
    /// historic fully-spaced style.</summary>
    public bool TightSymbolicOperators { get; set; } = true;
}
