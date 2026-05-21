namespace Shumway.Core;

/// <summary>
/// Core-level runtime error raised by built-ins when they detect a
/// situation the ISO standard reports as an <c>error/2</c> term. Core
/// can't depend on the AST <c>Term</c> type, so the exception carries
/// the error symbolically: a <see cref="Kind"/> string identifying the
/// ISO error category (e.g. <c>"evaluation_error"</c>) and an optional
/// <see cref="Detail"/> with the kind-specific payload
/// (e.g. <c>"zero_divisor"</c>).
///
/// <para>The embedding layer's <c>catch/3</c> implementation translates
/// these into the proper <c>error(Kind(Detail), _)</c> Prolog term
/// before unifying with the catcher pattern. This lets every built-in
/// — regardless of which assembly it lives in — surface ISO-shaped
/// errors without the Builtins project taking a dependency on the
/// Embedding project.</para>
/// </summary>
public sealed class PrologRuntimeException : Exception
{
    public string Kind { get; }
    public string Detail { get; }

    public PrologRuntimeException(string kind, string detail = "")
        : base(string.IsNullOrEmpty(detail) ? kind : $"{kind}: {detail}")
    {
        Kind = kind;
        Detail = detail;
    }

    /// <summary>Builds the <c>existence_error</c> for a call to an
    /// undefined predicate, with <see cref="Detail"/> set to the
    /// <c>Name/Arity</c> indicator. Used wherever a call fails to resolve
    /// — the bytecode interpreter's <c>call</c> / <c>execute</c> dispatch,
    /// the IL tail-call resolver, and the in-engine meta-call — so a
    /// missing predicate is reported identically however it is reached.</summary>
    public static PrologRuntimeException UndefinedProcedure(int functorId)
    {
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        return new PrologRuntimeException("existence_error",
            (AtomTable.GetById(atomId)?.Name ?? "?") + "/" + arity);
    }
}
