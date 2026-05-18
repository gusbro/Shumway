using Shumway.Compiler.Lexer;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// Raised by <see cref="Parser"/> when the token stream cannot be assembled into a
/// well-formed term. Carries the source position so callers can render an indicator
/// back to the offending lexeme.
/// </summary>
public sealed class ParseException : Exception
{
    public SourcePosition Position { get; }

    public ParseException(string message, SourcePosition position)
        : base($"{position.Line}:{position.Column}: {message}")
    {
        Position = position;
    }
}
