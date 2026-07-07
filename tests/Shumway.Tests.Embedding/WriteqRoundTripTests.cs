using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 (Logtalk bring-up): <c>writeq</c> / <c>write_canonical</c> output
/// must re-read as the same term. Two defects surfaced by Logtalk's generated
/// scratch files:
/// <list type="bullet">
/// <item>the solo atoms <c>','</c> and <c>'.'</c> were written UNQUOTED (a bare
/// <c>,</c> is the argument separator, a bare <c>.</c> the end-of-clause token)
/// — <c>is_punctuation(',')</c> became the unreadable <c>is_punctuation(,)</c>;</item>
/// <item>small/large/whole-valued floats printed via .NET <c>"R"</c> as
/// <c>1E-05</c> / <c>1</c>, which the lexer reads as an integer + a variable —
/// so <c>1.0e-5</c> did not round-trip.</item>
/// </list>
/// </summary>
public class WriteqRoundTripTests
{
    private static string TermToAtom(PrologEngine e, string goalTerm)
    {
        var sol = e.Query($"term_to_atom({goalTerm}, A).");
        Assert.True(sol.Success);
        return ((AtomTerm)sol["A"]!).Name;
    }

    [Fact]
    public void Comma_Atom_IsQuoted()
    {
        var e = new PrologEngine();
        Assert.Equal("','", TermToAtom(e, "','"));
    }

    [Fact]
    public void Dot_Atom_IsQuoted()
    {
        var e = new PrologEngine();
        Assert.Equal("'.'", TermToAtom(e, "'.'"));
    }

    [Fact]
    public void CommaAtom_AsArgument_ReReadsEqual()
    {
        // is_punctuation(',') is exactly Logtalk's pattern: the comma atom in
        // argument position must round-trip.
        var e = new PrologEngine();
        var sol = e.Query(
            "term_to_atom(p(','), A), term_to_atom(T, A), T == p(',').");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Semicolon_And_Bang_StayUnquoted()
    {
        // ';' and '!' are valid solo atoms — writeq leaves them bare.
        var e = new PrologEngine();
        Assert.Equal(";", TermToAtom(e, "(;)"));
        Assert.Equal("!", TermToAtom(e, "!"));
    }

    [Theory]
    [InlineData("0.00001", "1.0e-05")]
    [InlineData("1.0", "1.0")]
    [InlineData("100000.0", "100000.0")]
    [InlineData("3.14159", "3.14159")]
    public void Float_RendersRoundTrippable(string input, string expected)
    {
        var e = new PrologEngine();
        Assert.Equal(expected, TermToAtom(e, input));
    }

    [Fact]
    public void Float_SmallMagnitude_ReReadsEqual()
    {
        // The bug: 1.0e-5 wrote as "1E-05", which the lexer tokenised as the
        // integer 1 followed by a variable E — not re-readable as a float.
        var e = new PrologEngine();
        var sol = e.Query(
            "term_to_atom(0.00001, A), term_to_atom(T, A), T =:= 0.00001.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void NumberCodes_SmallFloat_RoundTrips()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "number_codes(1.0e-5, Cs), number_codes(N, Cs), N =:= 1.0e-5.");
        Assert.True(sol.Success);
    }
}
