using Shumway.Compiler.Lexer;
using Xunit;

namespace Shumway.Tests.Compiler.Lexer;

public class LexerTests
{
    private static List<Token> Tokens(string source) =>
        new global::Shumway.Compiler.Lexer.Lexer(source).Tokenize().ToList();

    private static Token First(string source) => Tokens(source)[0];

    // ---------- Trivial cases ----------

    [Fact]
    public void Empty_ProducesOnlyEof()
    {
        var toks = Tokens("");
        Assert.Single(toks);
        Assert.Equal(TokenKind.Eof, toks[0].Kind);
    }

    [Fact]
    public void OnlyWhitespace_ProducesOnlyEof()
    {
        var toks = Tokens("   \t\n  ");
        Assert.Single(toks);
        Assert.Equal(TokenKind.Eof, toks[0].Kind);
    }

    // ---------- Atoms ----------

    [Fact]
    public void UnquotedAtom_LowercaseStart_PicksUpAlnumTail()
    {
        Token t = First("foo_bar123");
        Assert.Equal(TokenKind.Atom, t.Kind);
        Assert.Equal("foo_bar123", t.Text);
    }

    [Fact]
    public void QuotedAtom_PreservesInnerSpacesAndCase()
    {
        Token t = First("'Hello World'");
        Assert.Equal(TokenKind.Atom, t.Kind);
        Assert.Equal("Hello World", t.Text);
    }

    [Fact]
    public void QuotedAtom_DoubledQuoteIsLiteralSingleQuote()
    {
        Token t = First("'don''t'");
        Assert.Equal(TokenKind.Atom, t.Kind);
        Assert.Equal("don't", t.Text);
    }

    [Fact]
    public void QuotedAtom_BackslashEscapes_DecodeKnownSequences()
    {
        Token t = First("'a\\nb\\tc\\\\d'");
        Assert.Equal(TokenKind.Atom, t.Kind);
        Assert.Equal("a\nb\tc\\d", t.Text);
    }

    [Fact]
    public void QuotedAtom_Unterminated_Throws()
    {
        Assert.Throws<LexerException>(() => Tokens("'unterminated"));
    }

    [Theory]
    [InlineData(":-", ":-")]
    [InlineData("-->", "-->")]
    [InlineData("=..", "=..")]
    [InlineData("==", "==")]
    [InlineData("\\=", "\\=")]
    [InlineData("@<", "@<")]
    public void SymbolicAtom_IsMaximalRunOfGraphicChars(string source, string expected)
    {
        Token t = First(source);
        Assert.Equal(TokenKind.Atom, t.Kind);
        Assert.Equal(expected, t.Text);
    }

    [Theory]
    [InlineData("!")]
    [InlineData(";")]
    public void Cut_AndDisjunction_AreAtoms(string source)
    {
        Token t = First(source);
        Assert.Equal(TokenKind.Atom, t.Kind);
        Assert.Equal(source, t.Text);
    }

    // ---------- Variables ----------

    [Theory]
    [InlineData("X", "X")]
    [InlineData("_foo", "_foo")]
    [InlineData("_", "_")]
    [InlineData("Result42", "Result42")]
    public void Variable_NameIsPreservedAsWritten(string source, string expected)
    {
        Token t = First(source);
        Assert.Equal(TokenKind.Variable, t.Kind);
        Assert.Equal(expected, t.Text);
    }

    // ---------- Integers ----------

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("42", 42L)]
    [InlineData("1000000", 1_000_000L)]
    public void Integer_DecimalLiteral_IsParsed(string source, long expected)
    {
        Token t = First(source);
        Assert.Equal(TokenKind.Integer, t.Kind);
        Assert.Equal(expected, t.IntValue);
        Assert.Equal(source, t.Text);
    }

    [Theory]
    [InlineData("0xff", 255L)]
    [InlineData("0x10", 16L)]
    [InlineData("0XCAFE", 0xCAFEL)]
    public void Integer_HexLiteral_IsParsed(string source, long expected)
    {
        Token t = First(source);
        Assert.Equal(TokenKind.Integer, t.Kind);
        Assert.Equal(expected, t.IntValue);
    }

    [Fact]
    public void Integer_HexWithNoDigits_Throws()
    {
        Assert.Throws<LexerException>(() => Tokens("0x"));
    }

    [Theory]
    [InlineData("0'a", 'a')]
    [InlineData("0'Z", 'Z')]
    [InlineData("0' ", ' ')]
    [InlineData("0'\\n", '\n')]
    [InlineData("0'\\\\", '\\')]
    public void Integer_CharCodeLiteral_IsParsed(string source, int expected)
    {
        Token t = First(source);
        Assert.Equal(TokenKind.Integer, t.Kind);
        Assert.Equal((long)expected, t.IntValue);
    }

    // ---------- Floats ----------

    [Theory]
    [InlineData("3.14", 3.14)]
    [InlineData("1.5e10", 1.5e10)]
    [InlineData("2.0E-3", 2.0e-3)]
    [InlineData("0.5", 0.5)]
    public void Float_LiteralsAreParsed(string source, double expected)
    {
        Token t = First(source);
        Assert.Equal(TokenKind.Float, t.Kind);
        Assert.Equal(expected, t.FloatValue);
        Assert.Equal(source, t.Text);
    }

    [Fact]
    public void Integer_FollowedByDotThenSpace_IsIntegerAndDot()
    {
        // "1." is an integer 1 plus the clause-terminator dot, NOT a float.
        var toks = Tokens("1. ");
        Assert.Equal(3, toks.Count);
        Assert.Equal(TokenKind.Integer, toks[0].Kind);
        Assert.Equal(1L, toks[0].IntValue);
        Assert.Equal(TokenKind.Dot, toks[1].Kind);
        Assert.Equal(TokenKind.Eof, toks[2].Kind);
    }

    [Fact]
    public void Float_RequiresDigitsAfterE()
    {
        Assert.Throws<LexerException>(() => Tokens("1.5e"));
    }

    // ---------- Strings ----------

    [Fact]
    public void String_DecodesEscapesAndDoubledQuote()
    {
        Token t = First("\"hello\\n\\\"world\\\"\\t\"");
        Assert.Equal(TokenKind.String, t.Kind);
        Assert.Equal("hello\n\"world\"\t", t.Text);
    }

    [Fact]
    public void String_DoubledQuoteIsLiteralQuote()
    {
        Token t = First("\"say \"\"hi\"\"\"");
        Assert.Equal(TokenKind.String, t.Kind);
        Assert.Equal("say \"hi\"", t.Text);
    }

    [Fact]
    public void String_Unterminated_Throws()
    {
        Assert.Throws<LexerException>(() => Tokens("\"oops"));
    }

    // ---------- Numeric character escapes (ISO \xHH\ and \OOO\) ----------

    [Fact]
    public void String_HexEscape_DecodesAndBackslashTerminated()
    {
        // "\x1b\[31m" — ESC then the literal characters [ 3 1 m.
        Token t = First("\"\\x1b\\[31m\"");
        Assert.Equal(TokenKind.String, t.Kind);
        Assert.Equal("[31m", t.Text);
    }

    [Fact]
    public void QuotedAtom_OctalEscape_DecodesAndBackslashTerminated()
    {
        // '\33\A' — octal 33 = 27 (ESC), then literal A.
        Token t = First("'\\33\\A'");
        Assert.Equal(TokenKind.Atom, t.Kind);
        Assert.Equal("A", t.Text);
    }

    [Fact]
    public void HexEscape_MissingTerminator_Throws()
    {
        // No terminating backslash before the closing quote.
        Assert.Throws<LexerException>(() => Tokens("\"\\x1b\""));
    }

    [Fact]
    public void HexEscape_Empty_Throws()
    {
        Assert.Throws<LexerException>(() => Tokens("\"\\x\\\""));
    }

    [Fact]
    public void Escape_BareNul_StillDecodesToZero()
    {
        // \0 with no digits/terminator is the legacy NUL shorthand.
        Token t = First("'\\0'");
        Assert.Equal("\0", t.Text);
    }

    // ---------- Punctuation ----------

    [Fact]
    public void Punctuation_OneCharTokensProduceCorrectKinds()
    {
        var toks = Tokens("()[]{},|");
        Assert.Equal(
            new[] {
                TokenKind.LParen, TokenKind.RParen,
                TokenKind.LBracket, TokenKind.RBracket,
                TokenKind.LBrace, TokenKind.RBrace,
                TokenKind.Comma, TokenKind.Bar,
                TokenKind.Eof,
            },
            toks.Select(t => t.Kind));
    }

    [Fact]
    public void ClauseTerminator_DotFollowedByWhitespaceIsDot()
    {
        var toks = Tokens("foo.");
        Assert.Equal(TokenKind.Atom, toks[0].Kind);
        Assert.Equal(TokenKind.Dot, toks[1].Kind);
        Assert.Equal(TokenKind.Eof, toks[2].Kind);
    }

    [Fact]
    public void DotMidAtom_IsPartOfGraphicAtom()
    {
        // "=.." is a single symbolic atom, not "= . .".
        Token t = First("=..");
        Assert.Equal(TokenKind.Atom, t.Kind);
        Assert.Equal("=..", t.Text);
    }

    // ---------- Comments ----------

    [Fact]
    public void LineComment_IsSkippedToEndOfLine()
    {
        var toks = Tokens("foo % a comment\nbar");
        Assert.Equal(TokenKind.Atom, toks[0].Kind);
        Assert.Equal("foo", toks[0].Text);
        Assert.Equal(TokenKind.Atom, toks[1].Kind);
        Assert.Equal("bar", toks[1].Text);
    }

    [Fact]
    public void BlockComment_IsSkipped()
    {
        var toks = Tokens("foo /* hello\nworld */ bar");
        Assert.Equal("foo", toks[0].Text);
        Assert.Equal("bar", toks[1].Text);
    }

    [Fact]
    public void DotBeforeLineComment_IsDot()
    {
        // 'foo.% comment' — the dot terminates the clause even though no
        // whitespace separates it from the comment marker.
        var toks = Tokens("foo.%bar\n");
        Assert.Equal(TokenKind.Atom, toks[0].Kind);
        Assert.Equal(TokenKind.Dot, toks[1].Kind);
        Assert.Equal(TokenKind.Eof, toks[2].Kind);
    }

    // ---------- Position tracking ----------

    [Fact]
    public void SourcePosition_TracksLinesAndColumns()
    {
        var toks = Tokens("foo\n  bar");
        Assert.Equal(new SourcePosition(1, 1, 0), toks[0].Position);
        Assert.Equal(new SourcePosition(2, 3, 6), toks[1].Position);
    }

    [Fact]
    public void SourcePosition_CarriesIntoEof()
    {
        var toks = Tokens("foo");
        Token eof = toks[^1];
        Assert.Equal(TokenKind.Eof, eof.Kind);
        // Column is 4 (one past the last char of "foo").
        Assert.Equal(4, eof.Position.Column);
    }

    // ---------- Realistic Prolog snippet ----------

    [Fact]
    public void ClauseLike_RoundTripsAllTokenKinds()
    {
        var toks = Tokens("p(X, [H|T]) :- q(X), !, r(H, T).");
        var kinds = toks.Select(t => t.Kind).ToArray();
        Assert.Equal(
            new[] {
                TokenKind.Atom,        // p
                TokenKind.LParen,
                TokenKind.Variable,    // X
                TokenKind.Comma,
                TokenKind.LBracket,
                TokenKind.Variable,    // H
                TokenKind.Bar,
                TokenKind.Variable,    // T
                TokenKind.RBracket,
                TokenKind.RParen,
                TokenKind.Atom,        // :-
                TokenKind.Atom,        // q
                TokenKind.LParen,
                TokenKind.Variable,    // X
                TokenKind.RParen,
                TokenKind.Comma,
                TokenKind.Atom,        // !
                TokenKind.Comma,
                TokenKind.Atom,        // r
                TokenKind.LParen,
                TokenKind.Variable,    // H
                TokenKind.Comma,
                TokenKind.Variable,    // T
                TokenKind.RParen,
                TokenKind.Dot,
                TokenKind.Eof,
            },
            kinds);
    }
}
