using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// SICStus/Scryer library(atts) storage shim: the '$*_attr_list' / '$attr_modules'
/// primitives and the direct put_atts/3 & get_atts/3 API (over the engine's SWI-style
/// attributed variables), the foundation for loading atts-based libraries.
/// </summary>
public class AttsShimTests
{
    [Fact]
    public void PutGet_RoundTripPerModule()
    {
        var e = new PrologEngine();
        e.ConsultString("t(D) :- put_atts(V, m1, dom(5)), get_atts(V, m1, dom(D)).");
        Assert.Equal(5L, Assert.IsType<IntTerm>(e.Query("t(D).")["D"]).Value);
    }

    [Fact]
    public void Put_SameFunctorOverwrites_OthersKept()
    {
        var e = new PrologEngine();
        e.ConsultString(
            "t(F, B) :- put_atts(V, m1, foo(1)), put_atts(V, m1, bar(2)), "
            + "put_atts(V, m1, foo(9)), get_atts(V, m1, foo(F)), get_atts(V, m1, bar(B)).");
        var sol = e.Query("t(F, B).");
        Assert.Equal(9L, Assert.IsType<IntTerm>(sol["F"]).Value);   // foo overwritten
        Assert.Equal(2L, Assert.IsType<IntTerm>(sol["B"]).Value);   // bar kept
    }

    [Fact]
    public void Get_PresenceAndAbsenceModes()
    {
        var e = new PrologEngine();
        e.ConsultString(
            "present :- put_atts(V, m1, a(1)), get_atts(V, m1, +a(_)).\n" +
            "fresh_absent :- get_atts(V, m1, -a(_)).\n" +
            "removed :- put_atts(V, m1, a(1)), put_atts(V, m1, -a(_)), get_atts(V, m1, -a(_)).");
        Assert.True(e.Query("present.").Success);
        Assert.True(e.Query("fresh_absent.").Success);   // no attr on a fresh var
        Assert.True(e.Query("removed.").Success);         // removed -> absent
    }

    [Fact]
    public void GetAttrList_FlattensAllModules()
    {
        var e = new PrologEngine();
        e.ConsultString(
            "n(N) :- put_atts(V, m1, a(1)), put_atts(V, m2, b(2)), "
            + "'$get_attr_list'(V, Ls), length(Ls, N).");
        // one attr under each of m1 and m2 → two Module:Attr pairs.
        Assert.Equal(2L, Assert.IsType<IntTerm>(e.Query("n(N).")["N"]).Value);
    }

    [Fact]
    public void TermAttributedVariables_CollectsAttrVars()
    {
        var e = new PrologEngine();
        e.ConsultString(
            "n(N) :- put_atts(V, m1, a(1)), '$term_attributed_variables'(f(V, plain), Vs), "
            + "length(Vs, N).");
        Assert.Equal(1L, Assert.IsType<IntTerm>(e.Query("n(N).")["N"]).Value);
    }
}
