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

    /// <summary><c>error(representation_error(Flag), _)</c> — typically
    /// <c>representation_error(character_code)</c>,
    /// <c>representation_error(max_arity)</c>, or one of the other ISO
    /// implementation-defined flags from §7.12.2.f.</summary>
    public static Term RepresentationError(string flag) =>
        Wrap(new CompoundTerm(
            "representation_error",
            new Term[] { new AtomTerm(flag) }));

    /// <summary><c>error(syntax_error(Detail), _)</c> — raised by the
    /// reader and by <c>read_term/2,3</c>, <c>atom_to_term/3</c>,
    /// <c>number_codes/2</c> on a malformed input. <c>Detail</c> is an
    /// implementation-defined atom or compound describing the problem
    /// (e.g. <c>illegal_number</c>, <c>operator_expected</c>).</summary>
    public static Term SyntaxError(string detail) =>
        Wrap(new CompoundTerm(
            "syntax_error",
            new Term[] { new AtomTerm(detail) }));

    /// <summary><c>error(resource_error(Resource), _)</c> — raised when
    /// an implementation-defined resource is exhausted (heap, stack,
    /// trail). <c>Resource</c> is an implementation-defined atom.</summary>
    public static Term ResourceError(string resource) =>
        Wrap(new CompoundTerm(
            "resource_error",
            new Term[] { new AtomTerm(resource) }));

    /// <summary><c>error(system_error, _)</c> — the catch-all for I/O
    /// and host-OS failures that don't map to a more specific ISO kind.
    /// Per §7.12.2.j, the standard error term has no payload; callers
    /// who want to attach a detail message can use
    /// <see cref="SystemError(string)"/>, which emits
    /// <c>system_error(Detail)</c>.</summary>
    public static Term SystemError() =>
        Wrap(new AtomTerm("system_error"));

    /// <summary><c>error(system_error(Detail), _)</c> — the non-standard
    /// detail variant; useful for surfacing a .NET exception's
    /// <c>Message</c> through <c>catch/3</c> without losing it.</summary>
    public static Term SystemError(string detail) =>
        Wrap(new CompoundTerm(
            "system_error",
            new Term[] { new AtomTerm(detail) }));

    private static Term Wrap(Term kind) =>
        new CompoundTerm("error", new Term[] { kind, new VarTerm("_") });
}
