using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §6.4.2 (character conversion) and §8.14.9–10. Covers
/// <c>char_conversion/2</c> (registers an InChar → OutChar mapping
/// the lexer applies to the start of every unquoted token, gated on
/// the <c>char_conversion</c> flag) and <c>current_char_conversion/2</c>
/// (enumerates the active table). Identity mappings remove entries.
/// Chunk 152.
/// </summary>
public class CharConversionConformance
{
    private static Term Atom(string n) => new AtomTerm(n);

    [Fact]
    public void CharConversion_RegistersMapping_VisibleViaCurrent()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("char_conversion('a', 'b').").Success);
        var sol = e.Query("current_char_conversion('a', X).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("b"), sol["X"]);
    }

    [Fact]
    public void CharConversion_IdentityRemovesEntry()
    {
        var e = new PrologEngine();
        e.Query("char_conversion('a', 'b').");
        e.Query("char_conversion('a', 'a').");
        Assert.Empty(e.QueryAll("current_char_conversion('a', _).").ToList());
    }

    [Fact]
    public void CurrentCharConversion_EnumeratesAll_ViaBacktracking()
    {
        var e = new PrologEngine();
        e.Query("char_conversion('a', 'b'), char_conversion('c', 'd').");
        var sols = e.QueryAll("current_char_conversion(I, O).").ToList();
        Assert.Equal(2, sols.Count);
    }

    [Fact]
    public void CharConversion_RepresentationError_OnNonCharacterArg()
    {
        // §8.14.9.3.c: not a one-char atom → representation_error(character)
        // (current_char_conversion/2 is the one that uses type_error).
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(char_conversion(123, 'a'), error(representation_error(F), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("character"), sol["F"]);
    }

    [Fact]
    public void CharConversion_InstantiationError_OnUnboundArg()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(char_conversion(_, 'a'), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void LexerIntegration_AppliesConversion_WhenFlagOn()
    {
        // The directive form mirrors the runtime builtin and the
        // lexer honours it on subsequent consults.
        var e = new PrologEngine();
        e.Flags.CharConversionEnabled = true;
        e.Query("char_conversion('Q', 'q').");
        // 'Q' in an unquoted context (atom start) maps to 'q'.
        e.ConsultString("Q.");
        Assert.True(e.Query("q.").Success);
    }
}
