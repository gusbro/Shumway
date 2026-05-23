using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 131c (Phase 9 Stage A, step 3, files 3-5): the
/// <c>AtomListBuiltins</c>, <c>ListBuiltins</c> and <c>SortBuiltins</c>
/// contract-violation sites now raise catchable ISO-shaped errors.
/// </summary>
public class Chunk131cTests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    // ---------- length/2 ----------
    //
    // The prelude's length/2 (chunk 96) routes through '$list_length' for
    // (nonvar, ?) and '$make_var_list' for (-, int); the both-var case
    // falls through to '$length_enum' and enumerates lists of growing
    // length forever — which is the standard, the same as SWI. So there
    // isn't an ISO-error path to pin here. The AtomListBuiltins.Length
    // C# impl is dead code at this point (kept for completeness, plus
    // the chunk-131c update for consistency).

    // ---------- append/3 ----------

    [Fact]
    public void Append_AllUnbound_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(append(_L1, _L2, _L3), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    // ---------- atom_codes/2 ----------

    [Fact]
    public void AtomCodes_NonAtomFirstArg_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_codes(123, _Cs), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("atom"), sol["T"]);
    }

    [Fact]
    public void AtomCodes_NonIntListElement_RaisesTypeErrorCharacterCode()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_codes(_A, [foo]), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("character_code"), sol["T"]);
    }

    [Fact]
    public void AtomCodes_PartialList_RaisesTypeErrorList()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_codes(_A, [97 | non_nil]), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("list"), sol["T"]);
    }

    // ---------- atom_concat/3 ----------

    [Fact]
    public void AtomConcat_AllUnbound_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_concat(_A, _B, _C), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void AtomConcat_NonAtomThird_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_concat(_A, _B, 123), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("atom"), sol["T"]);
    }

    // ---------- nth0/3 / nth1/3 ----------

    [Fact]
    public void Nth0_VarIndex_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(nth0(_N, [a,b,c], _X), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void Nth1_NonIntIndex_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(nth1(foo, [a,b,c], _X), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("integer"), sol["T"]);
    }

    // ---------- reverse/2 ----------

    [Fact]
    public void Reverse_PartialList_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(reverse([1,2 | _T], _R), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    // ---------- sort/2 ----------

    [Fact]
    public void Sort_PartialList_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(sort([2,1 | _T], _S), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    // ---------- Happy paths still work ----------

    [Fact]
    public void Length_ProperList_StillWorks()
    {
        var e = new PrologEngine();
        var sol = e.Query("length([a,b,c], N).");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["N"]);
    }

    [Fact]
    public void AtomConcat_TwoAtoms_StillWorks()
    {
        var e = new PrologEngine();
        var sol = e.Query("atom_concat(foo, bar, C).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("foobar"), sol["C"]);
    }

    [Fact]
    public void Sort_RealList_StillWorks()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("sort([3,1,2,1], [1,2,3]).").Success);
    }

    // ---------- Context indicator ----------

    [Fact]
    public void ContextSlot_CarriesBuiltinIndicator()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(atom_codes(123, _Cs), error(_, Name/Arity), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("atom_codes"), sol["Name"]);
        Assert.Equal(Int(2), sol["Arity"]);
    }
}
