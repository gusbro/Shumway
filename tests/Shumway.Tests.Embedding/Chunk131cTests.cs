using System.Linq;
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
    public void Append_AllUnbound_EnumeratesLikePureAppend()
    {
        // Phase 33 (PrologToC corpus) — the chunk-131c instantiation_error
        // here was WRONG: pure append/3 never raises. All-unbound append
        // enumerates k-element splits (first solution L1 = [], L2 = L3),
        // and the open-list "hole closing" idiom append(Open, [], Open)
        // must succeed binding the tail hole to [] — the DEC-10 rdtok
        // tokenizer's dictionary trick.
        var e = new PrologEngine();
        Assert.True(e.Query("append(L1, L2, L3), L1 == [], L2 == L3.").Success);
        // Backtracking reaches the k=1 split: L1 = [X], L3 = [X | L2].
        // (Cut — the enumeration is unbounded, exactly like SWI's.)
        Assert.True(e.Query("append(A, B, C), A = [x], !, C == [x | B].").Success);
        // The hole-closing idiom.
        Assert.True(e.Query(
            "D = [a = 1, b = 2 | _Hole], append(D, [], D), "
            + "D == [a = 1, b = 2].").Success);
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
    public void AtomCodes_NonIntListElement_RaisesRepresentationError()
    {
        var e = new PrologEngine();
        // ISO §8.16.5.3.d: a code-list element that is not a character
        // code — wrong type or out of range alike — is
        // representation_error(character_code).
        var sol = e.Query(
            "catch(atom_codes(_A, [foo]), error(representation_error(F), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("character_code"), sol["F"]);
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
    public void Nth0_VarIndex_Enumerates()
    {
        // SWI/SICStus behaviour (chunk 346): a variable index enumerates every
        // position on backtracking, rather than raising instantiation_error —
        // real programs iterate lists this way (e.g. nth0(Row, Board, R)).
        var e = new PrologEngine();
        var pairs = e.QueryAll("nth0(N, [a, b, c], X).")
            .Select(s => (((IntTerm)s["N"]!).Value, ((AtomTerm)s["X"]!).Name)).ToList();
        Assert.Equal(new[] { (0L, "a"), (1L, "b"), (2L, "c") }, pairs);

        // A bound element finds (and enumerates) only its positions.
        var idx = e.QueryAll("nth0(N, [a, b, a], a).")
            .Select(s => ((IntTerm)s["N"]!).Value).ToList();
        Assert.Equal(new long[] { 0, 2 }, idx);
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
