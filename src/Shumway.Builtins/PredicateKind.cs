namespace Shumway.Builtins;

/// <summary>ADR-059 — what a program may do with a system predicate's name.
/// Declared with the predicate's documentation metadata, not inferred from
/// the language it is implemented in.</summary>
public enum PredicateKind
{
    /// <summary>Provided by the engine beyond ISO (<c>format/2</c>,
    /// <c>between/3</c>, <c>length/2</c>): a module may define its own, the
    /// global module may not. The default for a builtin written in C#.</summary>
    Engine = 0,

    /// <summary>A list or higher-order library predicate (<c>append/3</c>,
    /// <c>member/2</c>): a module may define its own, the global module may
    /// redefine it with a warning. The default for one the prelude defines in
    /// Prolog.</summary>
    Library,

    /// <summary>An ISO 13211-1 built-in predicate (with its corrigenda): static,
    /// never redefinable.</summary>
    Iso,

    /// <summary>An ISO control construct: never redefinable.</summary>
    Control,
}
