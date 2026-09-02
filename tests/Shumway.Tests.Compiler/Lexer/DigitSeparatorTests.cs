using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Lexer;
using Xunit;

namespace Shumway.Tests.Compiler.Lexer;

/// <summary>The digit separator: an underscore between two digits of a
/// number, optionally followed by a layout text sequence — <c>1_000</c>,
/// <c>1_ 000</c>, <c>1_ /*c*/ 000</c>, the shape WG17 accepted on
/// 2025-06-02. Whether it reaches floats is still open there; we accept it
/// (see the theory below) rather than read a plainly intended number as a
/// syntax error.</summary>
public class DigitSeparatorTests
{
    private static List<Token> Tokens(string source, bool separators = true)
    {
        var lexer = new global::Shumway.Compiler.Lexer.Lexer(source)
            { DigitSeparators = separators };
        return lexer.Tokenize().ToList();
    }

    private static Token Only(string source)
    {
        var toks = Tokens(source);
        Assert.Equal(TokenKind.Eof, toks[^1].Kind);
        Assert.Equal(2, toks.Count);            // the number and EOF
        return toks[0];
    }

    [Theory]
    [InlineData("1_000", 1000L)]                     // option 1
    [InlineData("1_0_0_0", 1000L)]
    [InlineData("1_ 000", 1000L)]                    // option 3
    [InlineData("0_\n01", 1L)]                       // issue #43
    [InlineData("1_\n\t 000", 1000L)]
    [InlineData("1_ /*c*/ 3", 13L)]                  // option 4
    [InlineData("1_ % c\n 3", 13L)]
    [InlineData("0xdead_ beef", 3735928559L)]
    [InlineData("0b1_0_1", 5L)]
    [InlineData("0o1_ 7", 15L)]
    public void Separator_JoinsTheDigitsAroundIt(string source, long expected)
    {
        Token t = Only(source);
        Assert.Equal(TokenKind.Integer, t.Kind);
        Assert.Equal(expected, t.IntValue);
    }

    [Fact]
    public void Separator_DoesNotAbsorbTheDigitsInsideItsComment()
    {
        // The digits of `/*2*/` are comment. Deleting underscores from the
        // source slice would read 123 here.
        Assert.Equal(13L, Only("1_ /*2*/ 3").IntValue);
    }

    [Theory]
    [InlineData("1_", "_")]                          // nothing follows
    [InlineData("1__0", "__0")]                      // option 2 is undecided
    [InlineData("1_ x", "_")]                        // no digit after the layout
    [InlineData("1_ ", "_")]
    public void WithoutAFollowingDigit_TheUnderscoreStartsAVariable(
        string source, string variable)
    {
        var toks = Tokens(source);
        Assert.Equal(TokenKind.Integer, toks[0].Kind);
        Assert.Equal(1L, toks[0].IntValue);
        Assert.Equal(TokenKind.Variable, toks[1].Kind);
        Assert.Equal(variable, toks[1].Text);
    }

    [Fact]
    public void HexSeparator_NeedsAFollowingHexDigit()
    {
        var toks = Tokens("0xf_ g");
        Assert.Equal(TokenKind.Integer, toks[0].Kind);
        Assert.Equal(15L, toks[0].IntValue);
        Assert.Equal(TokenKind.Variable, toks[1].Kind);
    }

    [Theory]
    [InlineData("1_1.25", 11.25)]          // the integer part
    [InlineData("1_000.5", 1000.5)]
    [InlineData("11.2_5", 11.25)]          // the fraction
    [InlineData("1.0e1_0", 1.0e10)]        // the exponent
    [InlineData("1_1.2_5e1_1", 11.25e11)]
    [InlineData("1.0e-1_0", 1.0e-10)]
    [InlineData("1_ /*c*/ 1.2_ 5", 11.25)]
    public void Float_TakesSeparatorsToo(string source, double expected)
    {
        Token t = Only(source);
        Assert.Equal(TokenKind.Float, t.Kind);
        Assert.Equal(expected, t.FloatValue);
    }

    [Fact]
    public void Exponent_StartsWithADigit_NotWithASeparator()
    {
        // `1.0e_5` has no exponent: the float is 1.0 (the same rewind `1.0e`
        // already takes) and what follows is the atom `e_5`.
        var toks = Tokens("1.0e_5");
        Assert.Equal(TokenKind.Float, toks[0].Kind);
        Assert.Equal(1.0, toks[0].FloatValue);
        Assert.Equal(TokenKind.Atom, toks[1].Kind);
        Assert.Equal("e_5", toks[1].Text);
    }

    [Fact]
    public void Disabled_LeavesTheUnderscoreToTheVariable()
    {
        var toks = Tokens("1_000", separators: false);
        Assert.Equal(1L, toks[0].IntValue);
        Assert.Equal(TokenKind.Variable, toks[1].Kind);
        Assert.Equal("_000", toks[1].Text);
    }

    [Fact]
    public void RewindingASeparatorKeepsThePositionsHonest()
    {
        // The scan past a rejected separator crosses newlines, and its
        // rewind has to put the line back or every later position is off.
        var toks = Tokens("1_ x\nfoo");
        Token foo = toks.First(t => t.Text == "foo");
        Assert.Equal(2, foo.Position.Line);
    }

    [Fact]
    public void AcceptedSeparator_LeavesTheFollowingTokenOnItsOwnLine()
    {
        var toks = Tokens("1_\n000\nfoo");
        Assert.Equal(1000L, toks[0].IntValue);
        Token foo = toks.First(t => t.Text == "foo");
        Assert.Equal(3, foo.Position.Line);
    }

    [Fact]
    public void UnterminatedCommentAfterASeparator_IsASyntaxError()
    {
        // Either reading of `1_ /*` ends in the same unterminated comment,
        // so the speculative scan must not swallow the exception.
        Assert.Throws<LexerException>(() => Tokens("1_ /* never closed"));
    }
}
