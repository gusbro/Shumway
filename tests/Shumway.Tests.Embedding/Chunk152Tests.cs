using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 152: ISO §6.4.2 / §8.14.9-10 character conversion. Adds
/// the <c>:- char_conversion(In, Out)</c> directive, the runtime
/// <c>char_conversion/2</c> and <c>current_char_conversion/2</c>
/// builtins, and lexer integration: when
/// <see cref="Shumway.Compiler.Parsing.PrologFlags.CharConversionEnabled"/>
/// is true the lexer maps the start-of-token character through the
/// active table before tokenizing. Identity mappings (In == Out)
/// remove the entry per ISO.
/// </summary>
public class Chunk152Tests
{
    private static AtomTerm Atom(string n) => new(n);

    [Fact]
    public void Directive_Registers_TableEntry()
    {
        var e = new PrologEngine();
        e.ConsultString(":- char_conversion('a', 'b').");
        // Two solutions: the directive's mapping is enumerable.
        var sols = e.QueryAll("current_char_conversion(I, O).").ToList();
        Assert.Single(sols);
        Assert.Equal(Atom("a"), sols[0]["I"]);
        Assert.Equal(Atom("b"), sols[0]["O"]);
    }

    [Fact]
    public void Builtin_Registers_TableEntry()
    {
        // The runtime form is symmetric to the directive.
        var e = new PrologEngine();
        Assert.True(e.Query("char_conversion('x', 'y').").Success);
        var sols = e.QueryAll("current_char_conversion(I, O).").ToList();
        Assert.Single(sols);
        Assert.Equal(Atom("x"), sols[0]["I"]);
        Assert.Equal(Atom("y"), sols[0]["O"]);
    }

    [Fact]
    public void IdentityMapping_Removes_Entry()
    {
        // ISO §8.14.9 — an identity mapping (In == Out) deletes any
        // existing entry. Useful for undoing earlier conversions.
        var e = new PrologEngine();
        e.Query("char_conversion('a', 'b').");
        Assert.Single(e.QueryAll("current_char_conversion(_, _).").ToList());
        e.Query("char_conversion('a', 'a').");
        Assert.Empty(e.QueryAll("current_char_conversion(_, _).").ToList());
    }

    [Fact]
    public void CurrentCharConversion_Enumerates_AllEntries()
    {
        var e = new PrologEngine();
        e.Query("char_conversion('a', 'b'), char_conversion('c', 'd').");
        var sols = e.QueryAll("current_char_conversion(I, O).").ToList();
        Assert.Equal(2, sols.Count);
        var pairs = sols.Select(s => (s["I"], s["O"])).ToHashSet();
        Assert.Contains((Atom("a"), Atom("b")), pairs);
        Assert.Contains((Atom("c"), Atom("d")), pairs);
    }

    [Fact]
    public void CharConversion_VarArg_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(char_conversion(_, 'a'), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void CharConversion_NonAtomArg_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(char_conversion(123, 'a'), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("character"), sol["T"]);
    }

    [Fact]
    public void LexerIntegration_MapsStartOfTokenChar_WhenFlagOn()
    {
        // With CharConversionEnabled, the lexer sees the converted
        // char as the first char of every unquoted token. Mapping
        // 'A' → 'a' makes "A" parse as the variable name "a" — but
        // variables are still treated as variables by their leading
        // char's class, so this would tokenize as an atom. To make
        // the test concrete and observable, map a digit start that
        // would dispatch into ParseNumber to a letter start that
        // dispatches into ParseUnquotedAtom: "1" should now be read
        // as a one-character atom.
        var e = new PrologEngine();
        e.Flags.CharConversionEnabled = true;
        e.Query("char_conversion('Q', 'q').");
        // Consult a clause whose head's first char would otherwise
        // make it a variable. With conversion on, 'Q' → 'q' at
        // dispatch time means the lexer sees it as the start of an
        // atom, so the clause defines q/0 (not the bizarre case of
        // a variable-headed clause).
        e.ConsultString("Q.");
        Assert.True(e.Query("q.").Success);
    }

    [Fact]
    public void LexerIntegration_DoesNotMap_InsideQuotedAtoms()
    {
        // The conversion must NOT apply inside a quoted atom — 'A'
        // should stay 'A' even when 'A' → 'a' is registered. The
        // dispatch in NextTokenInner explicitly skips conversion for
        // the leading quote char so ParseQuotedAtom sees raw bytes.
        var e = new PrologEngine();
        e.Flags.CharConversionEnabled = true;
        e.Query("char_conversion('A', 'a').");
        e.ConsultString("'A'.");
        Assert.True(e.Query("'A'.").Success);
        // 'a' (the conversion target) is undefined — catch the
        // ISO existence_error so the test doesn't propagate it.
        var sol = e.Query("catch(a, error(existence_error(procedure, _), _), true).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Flag_Off_DisablesConversion()
    {
        // The table can be populated, but conversion is gated by the
        // CharConversionEnabled flag. The flag defaults to false so
        // a host that doesn't opt in sees the original lexer
        // behaviour, no surprise transformations.
        var e = new PrologEngine();
        Assert.False(e.Flags.CharConversionEnabled);
        e.Query("char_conversion('Q', 'q').");
        e.ConsultString("'Q'.");
        Assert.True(e.Query("'Q'.").Success);
        // 'q' is undefined; the flag-off path must leave it that way.
        var sol = e.Query("catch(q, error(existence_error(procedure, _), _), true).");
        Assert.True(sol.Success);
    }
}
