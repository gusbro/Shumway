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

    private ClauseOrError(Clause? clause, string? errorMessage, SourcePosition errorPosition)
    {
        Clause = clause;
        ErrorMessage = errorMessage;
        ErrorPosition = errorPosition;
    }

    public static ClauseOrError Ok(Clause clause)
        => new(clause, null, default);

    public static ClauseOrError Error(string message, SourcePosition position)
        => new(null, message, position);
}
