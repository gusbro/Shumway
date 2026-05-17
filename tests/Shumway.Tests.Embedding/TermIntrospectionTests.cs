using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 30 coverage: functor/3, arg/3, =../2 (univ), and ground/1.
/// These are the core ISO term-introspection builtins used by
/// meta-programming. Decomposition and composition modes are both
/// exercised; failure modes raise ISO-shaped errors.
/// </summary>
public class TermIntrospectionTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Compound(string f, params Term[] args) => new CompoundTerm(f, args);
    private static Term Nil() => new AtomTerm("[]");
    private static Term Cons(Term h, Term t) => new CompoundTerm(".", new[] { h, t });
    private static Term List(params Term[] items)
    {
        Term acc = Nil();
        for (int i = items.Length - 1; i >= 0; i--) acc = Cons(items[i], acc);
        return acc;
    }

    // ---------- functor/3 ----------

    [Fact]
    public void Functor_Decomposes_Compound()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("functor(foo(a, b, c), F, A).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("foo"), sol["F"]);
        Assert.Equal(Int(3), sol["A"]);
    }

    [Fact]
    public void Functor_Decomposes_Atom()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("functor(hello, F, A).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hello"), sol["F"]);
        Assert.Equal(Int(0), sol["A"]);
    }

    [Fact]
    public void Functor_Decomposes_Integer()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("functor(42, F, A).");
        Assert.True(sol.Success);
        Assert.Equal(Int(42), sol["F"]);
        Assert.Equal(Int(0), sol["A"]);
    }

    [Fact]
    public void Functor_Composes_FreshCompound()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("functor(T, foo, 3).");
        Assert.True(sol.Success);
        var t = Assert.IsType<CompoundTerm>(sol["T"]);
        Assert.Equal("foo", t.Functor);
        Assert.Equal(3, t.Args.Length);
        // All args are fresh unbound vars.
        Assert.All(t.Args, a => Assert.IsType<VarTerm>(a));
    }

    [Fact]
    public void Functor_Compose_ZeroArity_GivesAtom()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("functor(T, hello, 0).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hello"), sol["T"]);
    }

    // ---------- arg/3 ----------

    [Fact]
    public void Arg_PicksNthArgument()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("b"), engine.Query("arg(2, foo(a, b, c), X).")["X"]);
        Assert.Equal(Atom("a"), engine.Query("arg(1, foo(a, b, c), X).")["X"]);
        Assert.Equal(Atom("c"), engine.Query("arg(3, foo(a, b, c), X).")["X"]);
    }

    [Fact]
    public void Arg_OutOfRange_Fails()
    {
        // 0 and N > arity fall outside [1, arity]. Note: a negative
        // literal like -1 parses as the compound -(1), which is itself
        // a type error per ISO — covered separately.
        var engine = new PrologEngine();
        Assert.False(engine.Query("arg(0, foo(a, b), _).").Success);
        Assert.False(engine.Query("arg(3, foo(a, b), _).").Success);
    }

    [Fact]
    public void Arg_OnAtom_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("arg(1, hello, _).").Success);
    }

    // ---------- =../2 (univ) ----------

    [Fact]
    public void Univ_Decomposes_Compound()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("foo(a, b, c) =.. L.");
        Assert.True(sol.Success);
        Assert.Equal(
            List(Atom("foo"), Atom("a"), Atom("b"), Atom("c")),
            sol["L"]);
    }

    [Fact]
    public void Univ_Decomposes_Atom()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("hello =.. L.");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("hello")), sol["L"]);
    }

    [Fact]
    public void Univ_Composes_Compound()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("T =.. [bar, 1, 2].");
        Assert.True(sol.Success);
        Assert.Equal(
            Compound("bar", Int(1), Int(2)),
            sol["T"]);
    }

    [Fact]
    public void Univ_Composes_AtomFromSingletonList()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("T =.. [solo].");
        Assert.True(sol.Success);
        Assert.Equal(Atom("solo"), sol["T"]);
    }

    [Fact]
    public void Univ_BadListHead_Throws()
    {
        var engine = new PrologEngine();
        // First element must be an atom for multi-element lists.
        Assert.Throws<ShumwayPrologException>(
            () => engine.Query("T =.. [1, a, b]."));
    }

    // ---------- ground/1 ----------

    [Fact]
    public void Ground_GroundCompound_Succeeds()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("ground(foo(a, 1, bar(c))).").Success);
    }

    [Fact]
    public void Ground_AtomicTerms_Succeed()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("ground(hello).").Success);
        Assert.True(engine.Query("ground(42).").Success);
        Assert.True(engine.Query("ground([]).").Success);
    }

    [Fact]
    public void Ground_UnboundVar_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("ground(X).").Success);
    }

    [Fact]
    public void Ground_CompoundWithUnboundArg_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("ground(foo(a, X, c)).").Success);
    }

    [Fact]
    public void Ground_DeeplyNestedUnboundArg_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("ground(foo(bar(baz(X)))).").Success);
    }

    [Fact]
    public void Ground_ListWithUnboundElement_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("ground([1, 2, X, 4]).").Success);
    }
}
