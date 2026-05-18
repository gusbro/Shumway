using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

public class AtomListBuiltinsTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Cmp(string f, params Term[] args) => new CompoundTerm(f, args);
    private static Term Nil = new AtomTerm("[]");
    private static Term List(params Term[] elements)
    {
        Term result = Nil;
        for (int i = elements.Length - 1; i >= 0; i--)
            result = Cmp(".", elements[i], result);
        return result;
    }

    // ---------- length/2 ----------

    [Fact]
    public void Length_KnownList_BindsCount()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("length([a, b, c], N).");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["N"]);
    }

    [Fact]
    public void Length_EmptyList_IsZero()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("length([], N).");
        Assert.True(sol.Success);
        Assert.Equal(Int(0), sol["N"]);
    }

    [Fact]
    public void Length_KnownCount_BuildsListOfFreshVars()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("length(L, 3).");
        Assert.True(sol.Success);
        // L should be a list of three unbound variables.
        Assert.True(sol["L"] is CompoundTerm { Functor: "." });
        // Count via is_list:
        Assert.True(engine.Query("length(L, 3), is_list(L).").Success);
    }

    [Fact]
    public void Length_CheckExactCount()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("length([a, b, c], 3).").Success);
        Assert.False(engine.Query("length([a, b, c], 4).").Success);
    }

    [Fact]
    public void Length_BothUnbound_EnumeratesLengths()
    {
        // Chunk 43: length/2 moved to the prelude with full multi-mode
        // semantics — the both-free case now enumerates 0, 1, 2, … in
        // sync with the list growing one fresh var at a time. Take the
        // first three solutions and verify the (List, N) pairs.
        var engine = new PrologEngine();
        var sols = engine.QueryAll("length(L, N).").Take(3).ToList();
        Assert.Equal(3, sols.Count);
        Assert.Equal(new IntTerm(0), sols[0]["N"]);
        Assert.Equal(new IntTerm(1), sols[1]["N"]);
        Assert.Equal(new IntTerm(2), sols[2]["N"]);
    }

    // ---------- append/3 ----------

    [Fact]
    public void Append_TwoLiteralLists()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("append([a, b], [c, d], R).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("a"), Atom("b"), Atom("c"), Atom("d")), sol["R"]);
    }

    [Fact]
    public void Append_EmptyLeft_ReturnsRightAsIs()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("append([], [a, b], R).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("a"), Atom("b")), sol["R"]);
    }

    [Fact]
    public void Append_EmptyRight_ReturnsLeftAsIs()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("append([a, b], [], R).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("a"), Atom("b")), sol["R"]);
    }

    [Fact]
    public void Append_CheckExactResult_Succeeds()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("append([1, 2], [3, 4], [1, 2, 3, 4]).").Success);
    }

    [Fact]
    public void Append_CheckMismatchedResult_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("append([1, 2], [3, 4], [1, 2, 3]).").Success);
    }

    // ---------- atom_codes/2 ----------

    [Fact]
    public void AtomCodes_AtomToCodes()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_codes(abc, L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Int('a'), Int('b'), Int('c')), sol["L"]);
    }

    [Fact]
    public void AtomCodes_CodesToAtom()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_codes(A, [104, 105]).");      // 'h' 'i'
        Assert.True(sol.Success);
        Assert.Equal(Atom("hi"), sol["A"]);
    }

    [Fact]
    public void AtomCodes_EmptyAtom_EmptyList()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_codes('', L).");
        Assert.True(sol.Success);
        Assert.Equal(Nil, sol["L"]);
    }

    // ---------- atom_concat/3 ----------

    [Fact]
    public void AtomConcat_TwoAtoms_Produces_ConcatenatedAtom()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_concat(hello, world, R).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("helloworld"), sol["R"]);
    }

    [Fact]
    public void AtomConcat_CheckMatch_Succeeds()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("atom_concat(foo, bar, foobar).").Success);
        Assert.False(engine.Query("atom_concat(foo, bar, foo).").Success);
    }

    // ---------- compare/3 ----------

    [Fact]
    public void Compare_EqualAtoms()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("compare(O, foo, foo).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("="), sol["O"]);
    }

    [Fact]
    public void Compare_AtomsAlphabetically()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("<"), engine.Query("compare(O, abc, def).")["O"]);
        Assert.Equal(Atom(">"), engine.Query("compare(O, def, abc).")["O"]);
    }

    [Fact]
    public void Compare_NumberVsAtom_NumbersFirst()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("<"), engine.Query("compare(O, 42, foo).")["O"]);
        Assert.Equal(Atom(">"), engine.Query("compare(O, foo, 42).")["O"]);
    }

    [Fact]
    public void Compare_VariableVsEverything_VariablesFirst()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("<"), engine.Query("compare(O, X, 42).")["O"]);
        Assert.Equal(Atom("<"), engine.Query("compare(O, X, foo).")["O"]);
        Assert.Equal(Atom("<"), engine.Query("compare(O, X, foo(a)).")["O"]);
    }

    [Fact]
    public void Compare_CompoundVsAtom_CompoundsLast()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom(">"), engine.Query("compare(O, foo(a), foo).")["O"]);
    }

    [Fact]
    public void Compare_CompoundsByArity()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("<"), engine.Query("compare(O, foo(a), foo(a, b)).")["O"]);
    }

    [Fact]
    public void Compare_CompoundsSameArityByFunctor()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("<"), engine.Query("compare(O, abc(1), def(1)).")["O"]);
    }

    [Fact]
    public void Compare_CompoundsSameFunctorByArgs()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("<"), engine.Query("compare(O, foo(1), foo(2)).")["O"]);
        Assert.Equal(Atom(">"), engine.Query("compare(O, foo(b), foo(a)).")["O"]);
    }

    // ---------- @<, @>, @=<, @>= ----------

    [Fact]
    public void TermLess_BasicSuccessAndFailure()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("abc @< def.").Success);
        Assert.False(engine.Query("def @< abc.").Success);
        Assert.False(engine.Query("foo @< foo.").Success);
    }

    [Fact]
    public void TermGreaterOrEqual_AcceptsEquality()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("foo @>= foo.").Success);
        Assert.True(engine.Query("def @>= abc.").Success);
        Assert.False(engine.Query("abc @>= def.").Success);
    }

    [Fact]
    public void TermLessOrEqual_NumberVsCompound()
    {
        var engine = new PrologEngine();
        // Numbers < Compounds in standard order.
        Assert.True(engine.Query("42 @=< foo(1).").Success);
        Assert.False(engine.Query("foo(1) @=< 42.").Success);
    }

    // ---------- Integration: a Prolog program using the new builtins ----------

    [Fact]
    public void Integration_ReverseUsingAppend()
    {
        // reverse([], []).
        // reverse([H|T], R) :- reverse(T, RT), append(RT, [H], R).
        var engine = new PrologEngine();
        engine.ConsultString(
            "reverse([], []).\n" +
            "reverse([H|T], R) :- reverse(T, RT), append(RT, [H], R).\n");
        var sol = engine.Query("reverse([a, b, c], R).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("c"), Atom("b"), Atom("a")), sol["R"]);
    }
}
