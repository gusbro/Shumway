namespace Shumway.Compiler.Lexer;

/// <summary>
/// A single token in a Prolog source. Tokens are produced by <see cref="Lexer"/> and
/// consumed by the term parser. The fields populated depend on <see cref="Kind"/>:
///
/// <list type="bullet">
/// <item><see cref="TokenKind.Atom"/>, <see cref="TokenKind.Variable"/>,
///   <see cref="TokenKind.String"/>: <see cref="Text"/> holds the (decoded) name or
///   content.</item>
/// <item><see cref="TokenKind.Integer"/>: <see cref="IntValue"/> holds the parsed
///   value; <see cref="Text"/> holds the source representation.</item>
/// <item><see cref="TokenKind.Float"/>: <see cref="FloatValue"/> holds the parsed
///   value; <see cref="Text"/> holds the source representation.</item>
/// <item>Punctuation tokens (<see cref="TokenKind.LParen"/>, …, <see cref="TokenKind.Dot"/>):
///   <see cref="Text"/> holds the punctuation character verbatim.</item>
/// <item><see cref="TokenKind.Eof"/>: all value fields are empty.</item>
/// </list>
/// </summary>
public readonly record struct Token(
    TokenKind Kind,
    SourcePosition Position,
    string Text)
{
    public long IntValue { get; init; }
    public double FloatValue { get; init; }

    public override string ToString() => Kind switch
    {
        TokenKind.Integer => $"{Kind}({IntValue}) @ {Position}",
        TokenKind.Float => $"{Kind}({FloatValue}) @ {Position}",
        TokenKind.Eof => $"{Kind} @ {Position}",
        _ => $"{Kind}({Text}) @ {Position}",
    };
}
