using Shumway.Core;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Coverage for chunk 17: sort/2 + msort/2 in the Builtins layer, and
/// bagof/3 + setof/3 in the embedding layer (extending findall's
/// sub-engine machinery).
/// </summary>
public class SortAndCollectionTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Flt(double v) => new FloatTerm(v);
    private static Term Str(string s) => new StringTerm(s, TextKind.Codes);
    private static Term Nil() => new AtomTerm("[]");
    private static Term Cons(Term h, Term t) => new CompoundTerm(".", new[] { h, t });
    private static Term List(params Term[] items)
    {
        Term acc = Nil();
        for (int i = items.Length - 1; i >= 0; i--) acc = Cons(items[i], acc);
        return acc;
    }

    // ---------- sort/2 ----------

    [Fact]
    public void Sort_EmptyList_GivesEmptyList()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("sort([], L).");
        Assert.True(sol.Success);
        Assert.Equal(Nil(), sol["L"]);
    }

    [Fact]
    public void Sort_Atoms_AlphabeticalOrder()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("sort([cherry, apple, banana], L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("apple"), Atom("banana"), Atom("cherry")), sol["L"]);
    }

    [Fact]
    public void Sort_Integers_NumericOrder()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("sort([3, 1, 4, 1, 5, 9, 2, 6], L).");
        Assert.True(sol.Success);
        // Dedup removes the second 1.
        Assert.Equal(List(Int(1), Int(2), Int(3), Int(4), Int(5), Int(6), Int(9)), sol["L"]);
    }

    [Fact]
    public void Sort_Mixed_NumbersBeforeAtoms()
    {
        // Standard order: numbers < atoms.
        var engine = new PrologEngine();
        var sol = engine.Query("sort([b, 2, a, 1], L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Int(1), Int(2), Atom("a"), Atom("b")), sol["L"]);
    }

    [Fact]
    public void Sort_DropsDuplicates()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("sort([a, b, a, c, b, a], L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("a"), Atom("b"), Atom("c")), sol["L"]);
    }

    // ---------- msort/2 ----------

    [Fact]
    public void Msort_KeepsDuplicates()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("msort([a, b, a, c, b, a], L).");
        Assert.True(sol.Success);
        Assert.Equal(
            List(Atom("a"), Atom("a"), Atom("a"), Atom("b"), Atom("b"), Atom("c")),
            sol["L"]);
    }

    [Fact]
    public void Msort_OnIntegers_KeepsDuplicates()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("msort([3, 1, 4, 1, 5], L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Int(1), Int(1), Int(3), Int(4), Int(5)), sol["L"]);
    }

    // ---------- bagof/3 ----------

    [Fact]
    public void Bagof_NoSolutions_Fails()
    {
        var engine = new PrologEngine();
        engine.ConsultString("colour(red).");
        var sol = engine.Query("bagof(X, colour(blue), L).");
        Assert.False(sol.Success);
    }

    [Fact]
    public void Bagof_SingleSolution_BindsSingletonList()
    {
        var engine = new PrologEngine();
        engine.ConsultString("colour(red).");
        var sol = engine.Query("bagof(X, colour(X), L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("red")), sol["L"]);
    }

    [Fact]
    public void Bagof_MultipleSolutions_PreservesOrder()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a).
            p(b).
            p(c).
            """);
        var sol = engine.Query("bagof(X, p(X), L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("a"), Atom("b"), Atom("c")), sol["L"]);
    }

    [Fact]
    public void Bagof_ExistentialQuantifier_Stripped()
    {
        // Y^p(X, Y) — Y is existentially quantified. Without grouping
        // support our bagof treats every var as implicitly existential, so
        // this should just collect every X for which there's some Y.
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a, 1).
            p(b, 2).
            p(c, 3).
            """);
        var sol = engine.Query("bagof(X, Y^p(X, Y), L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("a"), Atom("b"), Atom("c")), sol["L"]);
    }

    [Fact]
    public void Bagof_KeepsDuplicates()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            v(1).
            v(2).
            v(1).
            v(3).
            """);
        var sol = engine.Query("bagof(X, v(X), L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Int(1), Int(2), Int(1), Int(3)), sol["L"]);
    }

    // ---------- setof/3 ----------

    [Fact]
    public void Setof_NoSolutions_Fails()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(a).");
        var sol = engine.Query("setof(X, p(blue), L).");
        Assert.False(sol.Success);
    }

    [Fact]
    public void Setof_SortsAndDedups()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            v(c).
            v(a).
            v(b).
            v(a).
            v(c).
            """);
        var sol = engine.Query("setof(X, v(X), L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("a"), Atom("b"), Atom("c")), sol["L"]);
    }

    [Fact]
    public void Setof_MixedTypes_StandardOrder()
    {
        // Numbers sort before atoms in standard order.
        var engine = new PrologEngine();
        engine.ConsultString("""
            item(banana).
            item(2).
            item(apple).
            item(1).
            """);
        var sol = engine.Query("setof(X, item(X), L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Int(1), Int(2), Atom("apple"), Atom("banana")), sol["L"]);
    }

    [Fact]
    public void Setof_CompoundTemplate_DedupsByStructure()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            edge(a, b).
            edge(c, d).
            edge(a, b).
            edge(b, c).
            """);
        var sol = engine.Query("setof(edge(X, Y), edge(X, Y), L).");
        Assert.True(sol.Success);
        Assert.Equal(
            List(
                new CompoundTerm("edge", new[] { Atom("a"), Atom("b") }),
                new CompoundTerm("edge", new[] { Atom("b"), Atom("c") }),
                new CompoundTerm("edge", new[] { Atom("c"), Atom("d") })),
            sol["L"]);
    }

    [Fact]
    public void Setof_WithExistential_StripsQuantifier()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(b, 1).
            p(a, 2).
            p(c, 3).
            p(a, 4).
            """);
        var sol = engine.Query("setof(X, Y^p(X, Y), L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("a"), Atom("b"), Atom("c")), sol["L"]);
    }
}
