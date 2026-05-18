using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §8.5 Term manipulation: <c>functor/3</c>, <c>arg/3</c>,
/// <c>=../2</c> (univ), plus the standard comparison family
/// (<c>==/2</c>, <c>\==/2</c>, <c>@&lt;/2</c>, <c>@&gt;/2</c>, <c>@=&lt;/2</c>,
/// <c>@&gt;=/2</c>).
/// </summary>
public class TermManipulationConformance
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Cmp(string f, params Term[] a) => new CompoundTerm(f, a);

    [Fact]
    public void Functor_Decompose()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("functor(foo(a, b, c), F, A).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("foo"), sol["F"]);
        Assert.Equal(Int(3), sol["A"]);
    }

    [Fact]
    public void Functor_AtomHasArityZero()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("functor(foo, F, A).");
        Assert.Equal(Atom("foo"), sol["F"]);
        Assert.Equal(Int(0), sol["A"]);
    }

    [Fact]
    public void Functor_Compose()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("functor(T, foo, 2).");
        Assert.True(sol.Success);
        var t = Assert.IsType<CompoundTerm>(sol["T"]);
        Assert.Equal("foo", t.Functor);
        Assert.Equal(2, t.Args.Length);
    }

    [Fact]
    public void Arg_FetchesPositionalArgument()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("b"), engine.Query("arg(2, foo(a, b, c), X).")["X"]);
        Assert.Equal(Atom("a"), engine.Query("arg(1, foo(a, b, c), X).")["X"]);
    }

    [Fact]
    public void Univ_Decompose()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("foo(a, b) =.. L.");
        Assert.True(sol.Success);
        // L = [foo, a, b]
        Assert.Equal(
            Cmp(".", Atom("foo"),
                Cmp(".", Atom("a"),
                    Cmp(".", Atom("b"), Atom("[]")))),
            sol["L"]);
    }

    [Fact]
    public void Univ_Compose()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("T =.. [foo, x, y].");
        var t = Assert.IsType<CompoundTerm>(sol["T"]);
        Assert.Equal("foo", t.Functor);
        Assert.Equal(2, t.Args.Length);
    }

    [Fact]
    public void StructEq_RequiresIdenticalTerms()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("foo == foo.").Success);
        Assert.True(engine.Query("foo(a) == foo(a).").Success);
        Assert.False(engine.Query("foo == bar.").Success);
        // Different variables compare unequal (even if both unbound).
        Assert.False(engine.Query("X == Y.").Success);
    }

    [Fact]
    public void StructNotEq()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("foo \\== bar.").Success);
        Assert.False(engine.Query("foo \\== foo.").Success);
    }

    [Fact]
    public void StandardOrder_NumberBelowAtomBelowCompound()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("1 @< foo.").Success);
        Assert.True(engine.Query("foo @< foo(a).").Success);
        Assert.True(engine.Query("foo @< foo(a, b).").Success);  // shorter arity first
    }

    [Fact]
    public void StandardOrder_NumbersByValue()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("1 @< 2.").Success);
        Assert.True(engine.Query("-5 @< 0.").Success);
    }
}
