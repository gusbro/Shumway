using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The native library(atts) storage primitives —
/// <c>'$put_to_attr_list'/3</c>, <c>'$get_from_attr_list'/3</c>,
/// <c>'$del_from_attr_list'/3</c> — replacing the prelude's Prolog walks
/// (the hottest predicates of a clpz solve). Semantics pinned against the
/// original shim: one attribute term per functor, first-functor-match
/// semidet get, no-op del on a miss, all mutations backtrackable.</summary>
public sealed class AttrListBuiltinTests
{
    [Fact]
    public void PutThenGet_RoundTripsByFunctor()
    {
        var e = new PrologEngine();
        var s = e.Query(
            "'$put_to_attr_list'(V, m, dom(1, 5)),"
            + " '$put_to_attr_list'(V, m, queue(a)),"
            + " '$get_from_attr_list'(V, m, dom(L, H)),"
            + " '$get_from_attr_list'(V, m, queue(Q)).");
        Assert.True(s.Success);
        Assert.Equal(1L, Assert.IsType<IntTerm>(s["L"]).Value);
        Assert.Equal(5L, Assert.IsType<IntTerm>(s["H"]).Value);
        Assert.Equal("a", Assert.IsType<AtomTerm>(s["Q"]).Name);
    }

    [Fact]
    public void Put_SameFunctor_Replaces()
    {
        var e = new PrologEngine();
        var s = e.Query(
            "'$put_to_attr_list'(V, m, dom(1, 5)),"
            + " '$put_to_attr_list'(V, m, dom(2, 3)),"
            + " '$get_from_attr_list'(V, m, dom(L, H)),"
            + " \\+ '$get_from_attr_list'(V, m, dom(1, 5)).");
        Assert.True(s.Success);
        Assert.Equal(2L, Assert.IsType<IntTerm>(s["L"]).Value);
        Assert.Equal(3L, Assert.IsType<IntTerm>(s["H"]).Value);
    }

    [Fact]
    public void Get_MissingFunctorOrPlainVar_Fails()
    {
        var e = new PrologEngine();
        Assert.False(e.Query(
            "'$put_to_attr_list'(V, m, dom(1)), '$get_from_attr_list'(V, m, other(_)).")
            .Success);
        Assert.False(e.Query("'$get_from_attr_list'(_, m, dom(_)).").Success);
        // Atom-shaped attribute terms (arity 0) work too.
        Assert.True(e.Query(
            "'$put_to_attr_list'(V, m, flagged), '$get_from_attr_list'(V, m, flagged).")
            .Success);
    }

    [Fact]
    public void Del_RemovesOnlyItsFunctor_LastOneDropsTheModule()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "'$put_to_attr_list'(V, m, dom(1)), '$put_to_attr_list'(V, m, queue(q)),"
            + " '$del_from_attr_list'(V, m, dom(_)),"
            + " \\+ '$get_from_attr_list'(V, m, dom(_)),"
            + " '$get_from_attr_list'(V, m, queue(q)).").Success);
        // Removing the last attribute demotes the variable to plain.
        Assert.True(e.Query(
            "'$put_to_attr_list'(V, m, dom(1)), '$del_from_attr_list'(V, m, dom(_)),"
            + " \\+ attvar(V), var(V).").Success);
        // A miss (or non-attvar) is a silent no-op.
        Assert.True(e.Query(
            "'$put_to_attr_list'(V, m, dom(1)), '$del_from_attr_list'(V, m, other(_)),"
            + " '$get_from_attr_list'(V, m, dom(1)).").Success);
        Assert.True(e.Query("'$del_from_attr_list'(_, m, dom(_)).").Success);
    }

    [Fact]
    public void Mutations_RevertOnBacktracking()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "'$put_to_attr_list'(V, m, dom(1)),"
            + " ( '$put_to_attr_list'(V, m, dom(9)), fail ; true ),"
            + " '$get_from_attr_list'(V, m, dom(1)).").Success);
        Assert.True(e.Query(
            "'$put_to_attr_list'(V, m, dom(1)),"
            + " ( '$del_from_attr_list'(V, m, dom(_)), fail ; true ),"
            + " '$get_from_attr_list'(V, m, dom(1)).").Success);
    }

    [Fact]
    public void PutAtts_GetAtts_ThreeArgApi_StillWorks()
    {
        // The prelude's put_atts/3 & get_atts/3 route through the native
        // builtins; +/-/bare modes preserved.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "put_atts(V, m, +dom(2)), get_atts(V, m, dom(X)), X == 2,"
            + " put_atts(V, m, -dom(_)), get_atts(V, m, -dom(_)).").Success);
    }
}
