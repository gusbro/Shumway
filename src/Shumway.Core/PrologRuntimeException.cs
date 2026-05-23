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

    /// <summary>Name of the builtin whose Impl raised this exception.
    /// Stamped by the interpreter's <c>CallBuiltin</c> dispatch as the
    /// exception unwinds out of the Impl (chunk 130); <c>null</c> if the
    /// exception arose outside builtin dispatch (e.g. from the bytecode
    /// interpreter's own resolver) or has not yet reached a dispatch
    /// site. The translation in <c>MetaBuiltins.TranslateRuntimeError</c>
    /// fills the ISO Context slot with <c>BuiltinName/BuiltinArity</c>
    /// when both are set.</summary>
    public string? BuiltinName { get; private set; }

    /// <summary>Arity companion to <see cref="BuiltinName"/>.</summary>
    public int BuiltinArity { get; private set; }

    public PrologRuntimeException(string kind, string detail = "")
        : base(string.IsNullOrEmpty(detail) ? kind : $"{kind}: {detail}")
    {
        Kind = kind;
        Detail = detail;
    }

    /// <summary>Stamps the offending builtin's <c>Name/Arity</c> onto the
    /// exception, if not already set. Called by the interpreter dispatch
    /// as the exception unwinds out of a builtin Impl so the ISO error
    /// term's Context slot can carry the proper indicator. Idempotent —
    /// a re-stamp from an outer dispatch (e.g. a meta-call) is ignored
    /// so the innermost builtin's identity wins.</summary>
    public void StampBuiltin(string name, int arity)
    {
        if (BuiltinName is null)
        {
            BuiltinName = name;
            BuiltinArity = arity;
        }
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
