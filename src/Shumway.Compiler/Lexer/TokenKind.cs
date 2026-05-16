namespace Shumway.Compiler.Lexer;

/// <summary>
/// Coarse-grained classification of a Prolog lexeme. The lexer assigns one of these
/// kinds to every <see cref="Token"/> it produces. Operator-precedence (the difference
/// between <c>+</c>, <c>:-</c>, <c>=..</c>, etc.) is the parser's job — they all come
/// out of the lexer as <see cref="Atom"/>s with their text preserved.
/// </summary>
public enum TokenKind
{
    /// <summary>End of input. Always the last token in a stream.</summary>
    Eof,

    /// <summary>A regular or quoted or symbolic atom. <c>Text</c> holds the canonical
    /// (unescaped) name.</summary>
    Atom,

    /// <summary>A named variable (<c>X</c>, <c>_var</c>) or the anonymous variable
    /// (<c>_</c>). <c>Text</c> holds the variable's source name as-written.</summary>
    Variable,

    /// <summary>An integer literal — decimal, hexadecimal (<c>0x...</c>), or
    /// character code (<c>0'a</c>). <c>IntValue</c> holds the parsed value;
    /// <c>Text</c> retains the source representation.</summary>
    Integer,

    /// <summary>A floating-point literal. <c>FloatValue</c> holds the parsed value;
    /// <c>Text</c> retains the source representation.</summary>
    Float,

    /// <summary>A double-quoted string literal. <c>Text</c> holds the decoded
    /// content.</summary>
    String,

    LParen,        // (
    RParen,        // )
    LBracket,      // [
    RBracket,      // ]
    LBrace,        // {
    RBrace,        // }
    Comma,         // ,
    Bar,           // |
    /// <summary>The clause-terminating period — a <c>.</c> followed by whitespace,
    /// end-of-input, or the start of a comment. A <c>.</c> in any other position is
    /// part of a graphic atom (e.g. <c>=..</c>) or of a float literal.</summary>
    Dot,
}
