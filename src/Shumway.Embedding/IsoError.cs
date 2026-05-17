using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>
/// Constructors for the ISO-standard <c>error(Kind, Context)</c> terms.
/// User-facing builtins that detect contract violations throw a
/// <see cref="ShumwayPrologException"/> wrapping one of these so the
/// resulting term lines up with what other ISO Prologs report.
///
/// <para>The <c>Context</c> slot is conventionally either a free variable
/// (anonymous) or the offending culprit. Phase 1 uses a fresh
/// <see cref="VarTerm"/> in the context slot — adding richer context (the
/// offending built-in's name and arity, source position, etc.) is a later
/// chunk.</para>
/// </summary>
public static class IsoError
{
    /// <summary><c>error(type_error(ExpectedType, ActualValue), _)</c></summary>
    public static Term TypeError(string expectedType, Term actualValue) =>
        Wrap(new CompoundTerm(
            "type_error",
            new Term[] { new AtomTerm(expectedType), actualValue }));

    /// <summary><c>error(instantiation_error, _)</c></summary>
    public static Term InstantiationError() =>
        Wrap(new AtomTerm("instantiation_error"));

    /// <summary><c>error(existence_error(Kind, What), _)</c> — typically
    /// <c>existence_error(procedure, foo/3)</c>.</summary>
    public static Term ExistenceError(string kind, Term what) =>
        Wrap(new CompoundTerm(
            "existence_error",
            new Term[] { new AtomTerm(kind), what }));

    /// <summary><c>error(evaluation_error(Kind), _)</c> — typically
    /// <c>evaluation_error(zero_divisor)</c>.</summary>
    public static Term EvaluationError(string kind) =>
        Wrap(new CompoundTerm(
            "evaluation_error",
            new Term[] { new AtomTerm(kind) }));

    /// <summary><c>error(domain_error(Domain, Value), _)</c></summary>
    public static Term DomainError(string domain, Term value) =>
        Wrap(new CompoundTerm(
            "domain_error",
            new Term[] { new AtomTerm(domain), value }));

    /// <summary><c>error(permission_error(Op, ObjType, Obj), _)</c></summary>
    public static Term PermissionError(string operation, string objectType, Term obj) =>
        Wrap(new CompoundTerm(
            "permission_error",
            new Term[] { new AtomTerm(operation), new AtomTerm(objectType), obj }));

    private static Term Wrap(Term kind) =>
        new CompoundTerm("error", new Term[] { kind, new VarTerm("_") });
}
