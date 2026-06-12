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

    /// <summary>The BigInteger value for integer literals that exceed
    /// <see cref="long"/> range. <see cref="IntValue"/> is unset in this
    /// case; consult <see cref="HasBigValue"/> first.</summary>
    public System.Numerics.BigInteger BigValue { get; init; }

    /// <summary><c>true</c> for integer tokens whose source literal didn't
    /// fit in a long and was therefore captured in <see cref="BigValue"/>.</summary>
    public bool HasBigValue { get; init; }

    /// <summary>True iff at least one whitespace character or comment
    /// preceded this token in the source. The parser uses this for
    /// the ISO §6.4.7 function-call disambiguation (chunk 149):
    /// <c>foo(x)</c> is a compound, <c>foo (x)</c> is the atom
    /// <c>foo</c> followed by a parenthesised term.</summary>
    public bool HasLeadingWhitespace { get; init; }

    /// <summary>True for atom tokens produced from a QUOTED source form
    /// (<c>'...'</c>, or the Arity <c>$...$</c> form). The parser uses
    /// this to keep quoting-sensitive surface syntax honest (chunk 439):
    /// the Arity snip opener <c>[!</c> requires a BARE <c>!</c> — a
    /// quoted <c>'!'</c> after <c>[</c> is an ordinary list element
    /// (<c>['!', X]</c> is a two-element list, not a snip).</summary>
    public bool WasQuoted { get; init; }

    public override string ToString() => Kind switch
    {
        TokenKind.Integer when HasBigValue => $"{Kind}({BigValue}) @ {Position}",
        TokenKind.Integer => $"{Kind}({IntValue}) @ {Position}",
        TokenKind.Float => $"{Kind}({FloatValue}) @ {Position}",
        TokenKind.Eof => $"{Kind} @ {Position}",
        _ => $"{Kind}({Text}) @ {Position}",
    };
}
