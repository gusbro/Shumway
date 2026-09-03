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

    /// <summary>Non-null when the text was syntactically PERFECT but names a
    /// value the implementation cannot represent (e.g. <c>max_float</c> for a
    /// float literal past double range). Carriers that surface parse failures
    /// as ISO errors must then raise <c>representation_error(flaw)</c>, not
    /// <c>syntax_error</c>.</summary>
    public string? RepresentationFlaw { get; init; }

    public ParseException(string message, SourcePosition position)
        : base($"{position.Line}:{position.Column}: {message}")
    {
        Position = position;
    }
}
