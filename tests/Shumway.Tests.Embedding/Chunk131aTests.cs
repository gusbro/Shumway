using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 131a (Phase 9 Stage A, step 3, file 1): every contract violation
/// in <c>AtomCharBuiltins</c> now raises a catchable
/// <see cref="Shumway.Core.PrologRuntimeException"/> with an ISO-shaped
/// kind, replacing the uncatchable <see cref="System.InvalidOperationException"/>
/// the file used before Phase 9. The precedence rule from ISO §7.12.2
/// — instantiation_error before type_error before representation_error
/// — is honoured at every check site.
/// </summary>
public class Chunk131aTests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    // ---------- atom_length/2 ----------

    [Fact]
    public void AtomLength_VarFirstArg_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_length(_X, _N), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void AtomLength_NonAtomFirstArg_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_length(123, _N), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("atom"), sol["T"]);
    }

    // ---------- atom_chars/2 ----------

    [Fact]
    public void AtomChars_BothUnbound_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_chars(_A, _Cs), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void AtomChars_NonAtomNonVar_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_chars(123, _Cs), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("atom"), sol["T"]);
    }

    [Fact]
    public void AtomChars_NonSingleCharElement_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_chars(_A, [foo]), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("character"), sol["T"]);
    }

    // ---------- char_code/2 ----------

    [Fact]
    public void CharCode_BothUnbound_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(char_code(_C, _I), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void CharCode_MultiCharAtom_RaisesTypeErrorCharacter()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(char_code(abc, _I), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("character"), sol["T"]);
    }

    [Fact]
    public void CharCode_OutOfRangeCode_RaisesRepresentationError()
    {
        var e = new PrologEngine();
        // 1 << 24 is well outside char.MaxValue (0xFFFF).
        var sol = e.Query("catch(char_code(_C, 16777216), error(representation_error(F), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("character_code"), sol["F"]);
    }

    // ---------- number_codes/2 ----------

    [Fact]
    public void NumberCodes_NonNumberFirstArgWithListSecond_RaisesTypeError()
    {
        var e = new PrologEngine();
        // First arg is bound to a non-number; List path isn't taken.
        var sol = e.Query("catch(number_codes(foo, [49]), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("number"), sol["T"]);
    }

    [Fact]
    public void NumberCodes_ProperList_NonInt_RaisesRepresentationError()
    {
        // ISO §8.16.8.3.d: an element of a code list that is not a
        // character code — wrong type or out of range alike — is
        // representation_error(character_code). The chars side keeps
        // type_error(character, E); the standard is asymmetric on purpose.
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(number_codes(_N, [foo]), error(representation_error(F), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("character_code"), sol["F"]);
    }

    [Fact]
    public void NumberCodes_NonProperList_RaisesTypeErrorList()
    {
        var e = new PrologEngine();
        // Tail is not nil and not a list — partial list.
        var sol = e.Query("catch(number_codes(_N, [49 | non_nil]), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("list"), sol["T"]);
    }

    [Fact]
    public void NumberCodes_GarbageString_RaisesSyntaxError()
    {
        // The chunk-129 surfaced case, now reaching the ISO path through
        // the proper precedence checks.
        var e = new PrologEngine();
        var sol = e.Query("catch(number_codes(_N, [97,98,99]), error(syntax_error(D), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("illegal_number"), sol["D"]);
    }

    // ---------- sub_atom/5 ----------

    [Fact]
    public void SubAtom_VarFirstArg_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(sub_atom(_A, 0, 1, _, _S), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void SubAtom_NonAtomFirstArg_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(sub_atom(123, 0, 1, _, _S), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("atom"), sol["T"]);
    }

    // ---------- Indicator Context propagation (chunk 130 still works) ----------

    [Fact]
    public void ContextSlot_CarriesBuiltinIndicator()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(atom_length(123, _N), error(_, Name/Arity), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("atom_length"), sol["Name"]);
        Assert.Equal(Int(2), sol["Arity"]);
    }

    // ---------- Happy paths still work ----------

    [Fact]
    public void AtomLength_OnAtom_StillWorks()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("atom_length(hello, 5).").Success);
    }

    [Fact]
    public void NumberCodes_RoundTrip_StillWorks()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("number_codes(42, [52, 50]).").Success);
    }
}
