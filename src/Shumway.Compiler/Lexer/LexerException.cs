namespace Shumway.Compiler.Lexer;

/// <summary>
/// Raised by <see cref="Lexer"/> when a malformed token is encountered. The exception
/// carries the source <see cref="Position"/> so the host can render a pointer back to
/// the offending character.
/// </summary>
public sealed class LexerException : Exception
{
    public SourcePosition Position { get; }

    public LexerException(string message, SourcePosition position)
        : base($"{position.Line}:{position.Column}: {message}")
    {
        Position = position;
    }
}
