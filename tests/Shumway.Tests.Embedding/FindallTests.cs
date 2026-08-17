using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

public class FindallTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Nil() => new AtomTerm("[]");
    private static Term Cons(Term h, Term t) => new CompoundTerm(".", new[] { h, t });
    private static Term List(params Term[] items)
    {
        Term acc = Nil();
        for (int i = items.Length - 1; i >= 0; i--) acc = Cons(items[i], acc);
        return acc;
    }

    [Fact]
    public void Findall_NoSolutions_YieldsEmptyList()
    {
        var engine = new PrologEngine();
        engine.ConsultString("colour(red).");
        var sol = engine.Query("findall(X, colour(blue), L).");
        Assert.True(sol.Success);
        Assert.Equal(Nil(), sol["L"]);
    }

    [Fact]
    public void Findall_SingleSolution_YieldsSingletonList()
    {
        var engine = new PrologEngine();
        engine.ConsultString("colour(red).");
        var sol = engine.Query("findall(X, colour(X), L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("red")), sol["L"]);
    }

    [Fact]
    public void Findall_MultipleSolutions_YieldsAllInOrder()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a).
            p(b).
            p(c).
            """);
        var sol = engine.Query("findall(X, p(X), L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("a"), Atom("b"), Atom("c")), sol["L"]);
    }

    [Fact]
    public void Findall_TemplateIsCompound_CapturesBoundCompound()
    {
        // findall(point(X, Y), pos(X, Y), L) — captures the whole compound at
        // each solution, not just one of its vars.
        var engine = new PrologEngine();
        engine.ConsultString("""
            pos(1, 2).
            pos(3, 4).
            """);
        var sol = engine.Query("findall(point(X, Y), pos(X, Y), L).");
        Assert.True(sol.Success);
        Assert.Equal(
            List(
                new CompoundTerm("point", new[] { Int(1), Int(2) }),
                new CompoundTerm("point", new[] { Int(3), Int(4) })),
            sol["L"]);
    }

    [Fact]
    public void Findall_IntegerTemplate_CollectsIntegers()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            v(1).
            v(2).
            v(3).
            """);
        var sol = engine.Query("findall(N, v(N), L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Int(1), Int(2), Int(3)), sol["L"]);
    }

    [Fact]
    public void Findall_DoesNotBindGoalVariablesInCallingEngine()
    {
        // After findall returns, X must remain unbound in the calling engine —
        // the sub-engine's bindings shouldn't leak back.
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a).
            p(b).
            """);
        var sol = engine.Query("findall(Y, p(Y), L), X = X.");
        Assert.True(sol.Success);
        // X remains unbound — represented as a synthetic _G* var.
        var x = sol["X"];
        Assert.IsType<VarTerm>(x);
    }

    [Fact]
    public void Findall_EmptyTemplate_StillCountsSolutions()
    {
        // findall(unit, p(_), L) — template is the atom 'unit', so L's length
        // reflects how many times Goal succeeded.
        var engine = new PrologEngine();
        engine.ConsultString("""
            q(_).
            q(_).
            q(_).
            """);
        var sol = engine.Query("findall(unit, q(_), L).");
        Assert.True(sol.Success);
        Assert.Equal(
            List(Atom("unit"), Atom("unit"), Atom("unit")),
            sol["L"]);
    }

    [Fact]
    public void Findall_NestedFindall_Works()
    {
        // Outer findall enumerates X; inner findall, for each X, collects
        // every Y such that p(X, Y).
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a, 1).
            p(a, 2).
            p(b, 3).
            v(a).
            v(b).
            """);
        var sol = engine.Query(
            "findall(group(X, Ys), (v(X), findall(Y, p(X, Y), Ys)), L).");
        Assert.True(sol.Success);
        Assert.Equal(
            List(
                new CompoundTerm("group", new[] { Atom("a"), List(Int(1), Int(2)) }),
                new CompoundTerm("group", new[] { Atom("b"), List(Int(3)) })),
            sol["L"]);
    }
}
