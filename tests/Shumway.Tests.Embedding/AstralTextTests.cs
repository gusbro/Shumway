using System;
using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The astral-unicode arc: character-level operations answer in
/// CODE POINTS, not UTF-16 units. BMP atoms keep their exact unit-based
/// fast paths (Atom.Shape == Bmp); only astral-bearing text takes the
/// code-point walk.</summary>
public sealed class AstralTextTests
{
    private const string Emoji = "😀";   // 😀, two UTF-16 units

    [Fact]
    public void AtomLength_CountsCodePoints()
    {
        var e = new PrologEngine();
        var sol = e.Query($"atom_length('{Emoji}ok', L).");
        Assert.True(sol.Success);
        Assert.Equal(3L, ((IntTerm)sol["L"]!).Value);
    }

    [Fact]
    public void SubAtom_SlicesOnCodePointBoundaries()
    {
        var e = new PrologEngine();
        // The 1-code-point prefix is the WHOLE emoji, not half a pair.
        var sol = e.Query($"sub_atom('{Emoji}x', 0, 1, A, S).");
        Assert.True(sol.Success);
        Assert.Equal(Emoji, ((AtomTerm)sol["S"]!).Name);
        Assert.Equal(1L, ((IntTerm)sol["A"]!).Value);
    }

    [Fact]
    public void AtomConcat_EnumeratesCodePointSplits()
    {
        var e = new PrologEngine();
        // '😀x' has 2 characters → exactly 3 splits (never mid-pair).
        var sol = e.Query(
            $"findall(P-S, atom_concat(P, S, '{Emoji}x'), L), length(L, N).");
        Assert.True(sol.Success);
        Assert.Equal(3L, ((IntTerm)sol["N"]!).Value);
    }

    [Fact]
    public void PackedText_PresentsCodePoints()
    {
        // The PSTR packs UTF-16 units; its chars/codes PRESENTATION joins
        // surrogate pairs — one element per character, end to end.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "atom_chars('😀ok', L), length(L, 3), L == ['😀', o, k].").Success);
        Assert.True(e.Query(
            "atom_codes('😀ok', L), L == [128512, 111, 107].").Success);
        Assert.True(e.Query(
            "X = \"a😀b\", length(X, 3), X == [a, '😀', b], "
            + "compare(=, X, [a, '😀', b]).").Success);
        // Round trip through the packed representation and back.
        Assert.True(e.Query(
            "atom_chars(A, ['😀', o, k]), atom_chars(A, L), "
            + "L == ['😀', o, k], atom_length(A, 3).").Success);
    }

    [Fact]
    public void PackedText_WritesAsCharacters()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "with_output_to(atom(A), write(\"a😀b\")).");
        Assert.True(sol.Success);
        Assert.Equal("[a,😀,b]", ((AtomTerm)sol["A"]!).Name);
    }

    [Fact]
    public void StandardOrder_SortsAtomsByCodePoint()
    {
        // Unit-wise UTF-16 order puts an astral atom (high surrogates
        // D800–DBFF) BELOW U+E000–U+FFFF atoms; the standard order is by
        // code point, so the astral atom sorts above.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "X = '', Y = '😀', X @< Y, msort([Y, X, z], [z, X, Y]), "
            + "compare(<, f(X), f(Y)).").Success);
    }

    [Fact]
    public void UnicodeIdentifiers_LexAndRoundTrip()
    {
        // Every neighbouring engine accepts extended identifier letters:
        // lowercase-class letters start atoms, uppercase-class letters
        // start variables — BMP (Trealla 0252) and astral (Trealla 0556)
        // alike — and writeq prints such atoms unquoted, so they read back.
        var e = new PrologEngine();
        Assert.True(e.Query("X = 𝒶𝒶, atom(X), atom_length(X, 2).").Success);
        Assert.True(e.Query("X = öko, atom_length(X, 3).").Success);
        var sol = e.Query("𝒜V = 1.");   // astral-capital starts a VARIABLE
        Assert.True(sol.Success);
        Assert.Equal(1L, ((IntTerm)sol["𝒜V"]!).Value);
        var w = e.Query("with_output_to(atom(A), writeq(𝒶𝒶)).");
        Assert.True(w.Success);
        Assert.Equal("𝒶𝒶", ((AtomTerm)w["A"]!).Name);
    }

    [Fact]
    public void CharType_ClassifiesByUnicodeCategory()
    {
        // char_type/2 classifies over the full Unicode tables (the $ctype
        // bridge): letters, case pairs and punctuation work for BMP and
        // astral characters alike; digit(W) keeps decimal ASCII weights,
        // matching SWI.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "char_type('𐐨', alpha), char_type('𐐨', csym), "
            + "char_type('𐐀', upper('𐐨')), char_type('𐐨', lower('𐐀')), "
            + "char_type('😀', punct), "
            + "char_type(ñ, alpha), char_type(ñ, to_upper('Ñ')), "
            + "char_type('٥', alnum), \\+ char_type('٥', digit(_)), "
            + "char_type(a, alpha), char_type('A', upper(a)).").Success);
    }

    [Fact]
    public void StreamCharAndCodeIo_RoundTripsAstral()
    {
        var e = new PrologEngine();
        string tmp = Path.Combine(Path.GetTempPath(),
            "shumway-astral-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            string f = tmp.Replace('\\', '/');
            Assert.True(e.Query(
                $"open('{f}', write, W), put_char(W, '{Emoji}'), "
                + "put_char(W, x), put_code(W, 0x10400), close(W).").Success);
            var sol = e.Query(
                $"open('{f}', read, R), peek_char(R, P), get_char(R, C1), "
                + "get_char(R, C2), get_code(R, K), get_char(R, E), close(R).");
            Assert.True(sol.Success);
            Assert.Equal(Emoji, ((AtomTerm)sol["P"]!).Name);
            Assert.Equal(Emoji, ((AtomTerm)sol["C1"]!).Name);
            Assert.Equal("x", ((AtomTerm)sol["C2"]!).Name);
            Assert.Equal(0x10400L, ((IntTerm)sol["K"]!).Value);
            Assert.Equal("end_of_file", ((AtomTerm)sol["E"]!).Name);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void PeekChar_IsIdempotentAcrossThePushback()
    {
        // Peeking an astral char consumes and pushes back both units —
        // a second peek and the following get must see the same character,
        // and the stream position must not move until the get.
        var e = new PrologEngine();
        string tmp = Path.Combine(Path.GetTempPath(),
            "shumway-astral-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            string f = tmp.Replace('\\', '/');
            Assert.True(e.Query(
                $"open('{f}', write, W), put_char(W, '{Emoji}'), close(W).").Success);
            Assert.True(e.Query(
                $"open('{f}', read, R), peek_char(R, P), peek_char(R, P), "
                + $"get_char(R, P), P == '{Emoji}', close(R).").Success);
        }
        finally { File.Delete(tmp); }
    }
}
