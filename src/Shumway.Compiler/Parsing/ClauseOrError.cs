using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;

namespace Shumway.Compiler.Parsing;

/// <summary>Either a successfully parsed <see cref="Clause"/> or a
/// captured parse error. Yielded by
/// <see cref="ClauseReader.ReadAllCollectingErrors"/> so callers can
/// surface every diagnostic in a single pass instead of stopping at
/// the first <see cref="ParseException"/>.</summary>
public sealed class ClauseOrError
{
    public Clause? Clause { get; }
    public string? ErrorMessage { get; }
    public SourcePosition ErrorPosition { get; }
    public bool IsError => ErrorMessage is not null;

    /// <summary>True when the clause was not read because a LIMIT was reached
    /// rather than because the text is wrong — a term nested deeper than the
    /// reader's stack. The text may be perfectly good Prolog, so a caller that
    /// says "syntax error" about the rest must not say it about this one.</summary>
    public bool IsResourceLimit => ResourceDetail is not null;

    /// <summary>Which limit, for a caller that raises this as an ISO ball:
    /// the culprit of <c>resource_error(Culprit)</c>. Null unless the entry is
    /// a resource limit.</summary>
    public string? ResourceDetail { get; }

    private ClauseOrError(Clause? clause, string? errorMessage, SourcePosition errorPosition,
        string? resourceDetail = null)
    {
        Clause = clause;
        ErrorMessage = errorMessage;
        ErrorPosition = errorPosition;
        ResourceDetail = resourceDetail;
    }

    public static ClauseOrError Ok(Clause clause)
        => new(clause, null, default);

    public static ClauseOrError Error(string message, SourcePosition position)
        => new(null, message, position);

    public static ClauseOrError ResourceLimit(string detail, SourcePosition position)
        => new(null, $"resource_error({detail})", position, detail);
}
