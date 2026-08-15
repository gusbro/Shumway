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
}
