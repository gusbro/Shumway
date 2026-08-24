using System;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The character-code range is every Unicode SCALAR VALUE
/// (0..0x10FFFF minus the surrogate block) — the astral-unicode arc widened
/// it from the BMP. What the builders must never do is what they once did
/// silently: truncate a code to 16 bits and build a DIFFERENT character
/// (0x10400 → 0x400). Astral codes now build the real character; the values
/// that name no character (negatives, surrogates, above 0x10FFFF) still
/// raise <c>representation_error</c>.</summary>
public sealed class BmpCharacterCodeTests
{
    [Fact]
    public void AtomCodes_AstralCode_BuildsTheCharacter()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "X is 0x10400, atom_codes(A, [X]), atom_length(A, 1), "
            + "char_code(C, X), C == A.").Success);
    }

    [Fact]
    public void NumberCodesPath_AstralCode_IsAValidCharButNoDigit()
    {
        // The astral code is a legitimate CHARACTER now; what fails is the
        // number parse — a syntax_error, not a representation_error.
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(
            () => e.Query("X is 0x10400, number_codes(_, [0'1, X])."));
        Assert.Contains("syntax_error", ex.Message);
    }

    [Fact]
    public void SurrogateAndBeyondMax_StillRaise()
    {
        var e = new PrologEngine();
        var ex1 = Assert.ThrowsAny<Exception>(
            () => e.Query("X is 0xD800, atom_codes(_, [X])."));
        Assert.Contains("representation_error", ex1.Message);
        var ex2 = Assert.ThrowsAny<Exception>(
            () => e.Query("X is 0x110000, char_code(_, X)."));
        Assert.Contains("representation_error", ex2.Message);
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
    public void LexerHexEscape_AstralCodePoint_BuildsThePair()
    {
        // '\x1F600\' used to append (char)0x1F600 truncated to 16 bits,
        // silently manufacturing U+F600; then it was guarded as an error;
        // now it builds the real character's surrogate pair.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "X = 'a\\x1F600\\b', atom_length(X, 3), "
            + "sub_atom(X, 1, 1, 1, C), char_code(C, 0x1F600).").Success);
        // The same escape inside a double-quoted string consults, and the
        // packed list counts CODE POINTS: three characters.
        e.ConsultString("p(\"a\\x10400\\b\").");
        Assert.True(e.Query("p(L), length(L, 3).").Success);
        // A surrogate escape names no character — still an error.
        var ex = Assert.ThrowsAny<Exception>(
            () => e.Query("X = 'a\\xD800\\b'."));
        Assert.Contains("not a Unicode character", ex.Message);
    }

    [Fact]
    public void LexerCharCodeLiteral_RawAstral_IsOneCodePoint()
    {
        // 0'😀 spans two UTF-16 units in the source; the literal denotes
        // the single code point 0x1F600, not the high-surrogate unit.
        var e = new PrologEngine();
        Assert.True(e.Query("X is 0'😀, X =:= 128512.").Success);
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
