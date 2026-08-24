using System;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The character-code range is the BMP (0..0xFFFF), and every atom
/// builder enforces it the way <c>char_code/2</c> always did. Before this,
/// <c>atom_codes(A, [0x10400])</c> silently truncated the code to 16 bits —
/// building a DIFFERENT character (0x400) with no error anywhere. This is
/// also what makes the Logtalk adapter's <c>unicode: bmp</c> claim true.</summary>
public sealed class BmpCharacterCodeTests
{
    [Fact]
    public void AtomCodes_AstralCode_RaisesRatherThanTruncating()
    {
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(
            () => e.Query("X is 0x10400, atom_codes(_, [X])."));
        Assert.Contains("representation_error", ex.Message);
    }

    [Fact]
    public void NumberCodesPath_AstralCode_RaisesToo()
    {
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(
            () => e.Query("X is 0x10400, number_codes(_, [0'1, X])."));
        Assert.Contains("representation_error", ex.Message);
    }

    [Fact]
    public void NegativeCode_RaisesToo()
    {
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(
            () => e.Query("atom_codes(_, [-1])."));
        Assert.Contains("representation_error", ex.Message);
    }

    [Fact]
    public void FullBmp_RoundTrips()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "X is 0x4E2D, atom_codes(A, [X]), atom_codes(A, [Y]), atom_length(A, L).");
        Assert.True(sol.Success);
        Assert.Equal(0x4E2DL, ((IntTerm)sol["Y"]!).Value);
        Assert.Equal(1L, ((IntTerm)sol["L"]!).Value);
    }
    [Fact]
    public void LexerHexEscape_AstralCodePoint_RaisesRatherThanTruncating()
    {
        // '\x1F600\' used to append (char)0x1F600 — truncated to 16
        // bits, silently manufacturing U+F600 (a private-use character).
        // Same guard family as the builders above, at the lexer.
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(
            () => e.Query("X = 'a\\x1F600\\b'."));
        Assert.Contains("above the BMP", ex.Message);
        var ex2 = Assert.ThrowsAny<Exception>(
            () => e.ConsultString("p(\"a\\x10400\\b\")."));
        Assert.Contains("above the BMP", ex2.Message);
    }

    [Fact]
    public void LexerEscapes_BmpAndCharCodeLiteral_StillWork()
    {
        var e = new PrologEngine();
        // A BMP escape still builds the character...
        Assert.True(e.Query(
            "X = '\\x1b\\', char_code(X, 27).").Success);
        // ...and 0'\x…\ denotes an INTEGER code, where an astral
        // value is a perfectly good integer — no atom is built.
        Assert.True(e.Query("X is 0'\\x1F600\\, X =:= 128512.").Success);
    }

}
