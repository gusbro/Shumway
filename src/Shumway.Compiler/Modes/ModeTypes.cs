namespace Shumway.Compiler.Modes;

/// <summary>
/// The mode-analysis data model. ADR-012 defined the
/// <c>:- mode/1</c> directive (originally parsed and stored, not
/// exploited); this type set is the
/// shared vocabulary the embedding-layer parser produces and the
/// compiler's specialised code generation will consume.
///
/// <para>Lives in <c>Shumway.Compiler</c> so both
/// <c>Shumway.Embedding</c> (which parses the directive) and the WAM /
/// IL compilers (which will specialise on it) can reference it
/// without a project-reference cycle.</para>
/// </summary>
public enum ModeIndicator
{
    /// <summary><c>+</c> — input. Bound (non-variable) at call time.</summary>
    Input,

    /// <summary><c>-</c> — output. Unbound at call time; bound by the
    /// predicate.</summary>
    Output,

    /// <summary><c>?</c> — either. May be bound or unbound. The default
    /// when no mode declaration covers an argument.</summary>
    Either,
}

/// <summary>Determinism categories from Mercury (ADR-012). The
/// annotation is optional on a <c>:- mode</c> directive; absence is
/// <see cref="NoneDeclared"/>, which the analysis treats as the most
/// permissive (<see cref="Nondet"/>) for safety.</summary>
public enum Determinism
{
    /// <summary>Exactly one solution.</summary>
    Det,

    /// <summary>Zero or one solution; no backtracking on success.</summary>
    Semidet,

    /// <summary>One or more solutions.</summary>
    Multi,

    /// <summary>Zero or more solutions (the most permissive).</summary>
    Nondet,

    /// <summary>No <c>is ...</c> annotation was given. Treated as
    /// <see cref="Nondet"/> by the analysis but kept distinct so
    /// tooling can tell "declared nondet" from "didn't say".</summary>
    NoneDeclared,
}

/// <summary>
/// One <c>:- mode</c> declaration: the predicate it covers (by global
/// functor id), the per-argument mode indicators, and the optional
/// determinism annotation. A predicate may carry several of these —
/// one per callable mode (ADR-012's append/3 example has three).
/// </summary>
public sealed class ModeDeclaration
{
    /// <summary>Global functor id (name + arity) the declaration
    /// covers. The arity is implied by <see cref="ArgModes"/>.Count.</summary>
    public int FunctorId { get; }

    /// <summary>Per-argument mode indicators, in argument order.</summary>
    public IReadOnlyList<ModeIndicator> ArgModes { get; }

    /// <summary>The declared determinism, or
    /// <see cref="Modes.Determinism.NoneDeclared"/> when the directive
    /// had no <c>is ...</c> annotation.</summary>
    public Determinism Determinism { get; }

    public ModeDeclaration(
        int functorId, IReadOnlyList<ModeIndicator> argModes, Determinism determinism)
    {
        FunctorId = functorId;
        ArgModes = argModes;
        Determinism = determinism;
    }

    /// <summary>Arity covered by this declaration.</summary>
    public int Arity => ArgModes.Count;

    /// <summary>The effective determinism for analysis: an undeclared
    /// annotation is treated as <see cref="Modes.Determinism.Nondet"/>
    /// — the safe assumption that the predicate may produce any number
    /// of solutions.</summary>
    public Determinism EffectiveDeterminism =>
        Determinism == Determinism.NoneDeclared ? Determinism.Nondet : Determinism;

    /// <summary>True when the declaration promises at most one
    /// solution — the cases where specialised code generation can drop
    /// choice-point machinery. <see cref="Modes.Determinism.Det"/> and
    /// <see cref="Modes.Determinism.Semidet"/> qualify.</summary>
    public bool IsDeterministic =>
        EffectiveDeterminism is Determinism.Det or Determinism.Semidet;
}
