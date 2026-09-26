using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Constructors for the ISO-standard <c>error(Kind, Context)</c> terms.
/// User-facing builtins that detect contract violations throw a
/// <see cref="ShumwayPrologException"/> wrapping one of these so the
/// resulting term lines up with what other ISO Prologs report.
///
/// <para>The <c>Context</c> slot is impl-defined (§7.12.2) and holds the
/// <c>Name/Arity</c> indicator of the builtin that raised the error. A
/// factory called with an <see cref="Activation"/> fills it at once from
/// <see cref="Activation.CurrentBuiltinName"/>; one called without leaves
/// <see cref="OpenContext"/>, which the engine fills the same way when the
/// ball surfaces (<see cref="CloseContext"/>), so a builtin need not thread
/// its engine through every check to be named.</para>
/// </summary>
public static class IsoError
{
    /// <summary><c>error(type_error(ExpectedType, ActualValue), Context)</c></summary>
    public static Term TypeError(string expectedType, Term actualValue, Activation? engine = null) =>
        Wrap(new CompoundTerm(
            "type_error",
            new Term[] { new AtomTerm(expectedType), actualValue }),
            engine);

    /// <summary><c>error(instantiation_error, Context)</c></summary>
    /// <summary><c>error(uninstantiation_error(Culprit), Ctx)</c> — an
    /// argument that must be UNBOUND was bound (ISO Cor.2; asserta/2's
    /// reference output is the canonical case).</summary>
    public static Term UninstantiationError(Term culprit, Activation? engine = null) =>
        Wrap(new CompoundTerm("uninstantiation_error", new[] { culprit }), engine);

    public static Term InstantiationError(Activation? engine = null) =>
        Wrap(new AtomTerm("instantiation_error"), engine);

    /// <summary><c>error(existence_error(Kind, What), Context)</c> — typically
    /// <c>existence_error(procedure, foo/3)</c>.</summary>
    public static Term ExistenceError(string kind, Term what, Activation? engine = null) =>
        Wrap(new CompoundTerm(
            "existence_error",
            new Term[] { new AtomTerm(kind), what }),
            engine);

    /// <summary><c>error(evaluation_error(Kind), Context)</c> — typically
    /// <c>evaluation_error(zero_divisor)</c>.</summary>
    public static Term EvaluationError(string kind, Activation? engine = null) =>
        Wrap(new CompoundTerm(
            "evaluation_error",
            new Term[] { new AtomTerm(kind) }),
            engine);

    /// <summary><c>error(domain_error(Domain, Value), Context)</c></summary>
    public static Term DomainError(string domain, Term value, Activation? engine = null) =>
        Wrap(new CompoundTerm(
            "domain_error",
            new Term[] { new AtomTerm(domain), value }),
            engine);

    /// <summary><c>error(permission_error(Op, ObjType, Obj), Context)</c></summary>
    public static Term PermissionError(string operation, string objectType, Term obj, Activation? engine = null) =>
        Wrap(new CompoundTerm(
            "permission_error",
            new Term[] { new AtomTerm(operation), new AtomTerm(objectType), obj }),
            engine);

    /// <summary><c>error(representation_error(Flag), Context)</c> — typically
    /// <c>representation_error(character_code)</c>,
    /// <c>representation_error(max_arity)</c>, or one of the other ISO
    /// implementation-defined flags from §7.12.2.f.</summary>
    public static Term RepresentationError(string flag, Activation? engine = null) =>
        Wrap(new CompoundTerm(
            "representation_error",
            new Term[] { new AtomTerm(flag) }),
            engine);

    /// <summary><c>error(syntax_error(Detail), Context)</c> — raised by the
    /// reader and by <c>read_term/2,3</c>, <c>atom_to_term/3</c>,
    /// <c>number_codes/2</c> on a malformed input. <c>Detail</c> is an
    /// implementation-defined atom or compound describing the problem
    /// (e.g. <c>illegal_number</c>, <c>operator_expected</c>).</summary>
    public static Term SyntaxError(string detail, Activation? engine = null) =>
        Wrap(new CompoundTerm(
            "syntax_error",
            new Term[] { new AtomTerm(detail) }),
            engine);

    /// <summary><c>error(resource_error(Resource), Context)</c> — raised when
    /// an implementation-defined resource is exhausted (heap, stack,
    /// trail). <c>Resource</c> is an implementation-defined atom.</summary>
    public static Term ResourceError(string resource, Activation? engine = null) =>
        Wrap(new CompoundTerm(
            "resource_error",
            new Term[] { new AtomTerm(resource) }),
            engine);

    /// <summary><c>error(system_error, Context)</c> — the catch-all for I/O
    /// and host-OS failures that don't map to a more specific ISO kind.
    /// Per §7.12.2.j, the standard system_error term has no payload;
    /// callers who want to attach a detail message can use
    /// <see cref="SystemError(string, Activation)"/>, which emits
    /// <c>system_error(Detail)</c>.</summary>
    public static Term SystemError(Activation? engine = null) =>
        Wrap(new AtomTerm("system_error"), engine);

    /// <summary><c>error(system_error(Detail), Context)</c> — the
    /// non-standard detail variant; useful for surfacing a .NET
    /// exception's <c>Message</c> through <c>catch/3</c> without losing
    /// it.</summary>
    public static Term SystemError(string detail, Activation? engine = null) =>
        Wrap(new CompoundTerm(
            "system_error",
            new Term[] { new AtomTerm(detail) }),
            engine);

    private static Term Wrap(Term kind, Activation? engine) =>
        new CompoundTerm("error", new Term[] { kind, BuildContext(engine) });

    /// <summary>Builds the impl-defined Context term — <c>Name/Arity</c>
    /// of the builtin currently executing, when the engine carries one;
    /// a fresh anonymous variable otherwise. The indicator form
    /// <c>'/'/2</c> is what other ISO Prologs (SWI, SICStus) put here
    /// and is what catchers using <c>error(_, Name/Arity)</c> patterns
    /// expect.</summary>
    private static Term BuildContext(Activation? engine)
    {
        if (engine?.CurrentBuiltinName is string name)
            return new CompoundTerm("/", new Term[]
            {
                new AtomTerm(name),
                new IntTerm(engine.CurrentBuiltinArity),
            });
        return OpenContext;
    }

    /// <summary>The context of a ball built with no engine in hand: one
    /// shared variable, recognised by identity when the ball surfaces
    /// (<see cref="CloseContext"/>), so the builtin that raised it is named
    /// then. A ball from <c>throw/1</c> never carries this instance, so a
    /// user's own variables stay as they are.</summary>
    internal static readonly VarTerm OpenContext = new("_");

    /// <summary>The ball with its context filled in, when the context is
    /// still <see cref="OpenContext"/> and the engine names a running
    /// builtin: <c>error(Kind, Name/Arity)</c>. Any other ball comes back
    /// as it is.</summary>
    internal static Term CloseContext(Term ball, Activation engine)
    {
        if (ball is not CompoundTerm { Functor: "error", Args.Length: 2 } e
            || !ReferenceEquals(e.Args[1], OpenContext)
            || engine.CurrentBuiltinName is not string name)
            return ball;
        return new CompoundTerm("error", new Term[]
        {
            e.Args[0],
            new CompoundTerm("/", new Term[]
            {
                new AtomTerm(name),
                new IntTerm(engine.CurrentBuiltinArity),
            }),
        }) { Position = e.Position };
    }
}
