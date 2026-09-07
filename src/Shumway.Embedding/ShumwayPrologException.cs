using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>
/// .NET-level exception used as the propagation vehicle for Prolog's
/// <c>throw/1</c>. The exception carries the user-supplied error
/// <see cref="Term"/> verbatim — the engine doesn't classify or normalise
/// it; classification lives in user code or in the catcher pattern of
/// <c>catch/3</c>.
///
/// <para>Most user code throws an <c>error/2</c> compound — see
/// <see cref="IsoError"/> for helpers that build the ISO-standard error
/// terms (<c>type_error</c>, <c>instantiation_error</c>,
/// <c>existence_error</c>, …) — but any term is permitted.</para>
/// </summary>
public sealed class ShumwayPrologException : Exception
{
    public Term Term { get; }

    /// <summary>True when the error was raised by host-side machinery
    /// between goals (the clause/goal-expansion processor), so whatever
    /// frames sit on the engine stack are LEFTOVERS from unrelated earlier
    /// work, not this error's origin. The trace capture skips them rather
    /// than report a bogus culprit (issue #97: <c>at bb_delete/2</c> under a
    /// <c>resource_error(expansion_depth)</c>).</summary>
    public bool EngineStackIsStale { get; init; }

    public ShumwayPrologException(Term term)
        : base("Prolog throw/1: " + term)
    {
        ArgumentNullException.ThrowIfNull(term);
        Term = term;
    }
}
