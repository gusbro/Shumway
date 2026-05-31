using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 240: <see cref="PrologEngine.Query{T}(string)"/> /
/// <see cref="PrologEngine.Query{T}(string,string)"/> typed queries
/// and the <see cref="PrologEngine.QueryFirst{T}(string)"/> /
/// <see cref="PrologEngine.QueryFirst{T}(string,string)"/>
/// single-solution variants. Each yields the named (or auto-
/// detected) variable's binding projected through
/// <see cref="PrologEngine.FromTerm{T}"/>.
/// </summary>
public class Chunk240Tests
{
    [Fact]
    public void Query_SingleVariable_AutoDetect_IntStream()
    {
        var engine = new PrologEngine();
        // between/3 is a standard builtin — emits 1..5.
        var values = engine.Query<int>("between(1, 5, X).").ToList();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, values);
    }

    [Fact]
    public void Query_ExplicitVariable_MultiSolutionStream()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public colour/1.\n"
            + "colour(red).\n"
            + "colour(green).\n"
            + "colour(blue).\n");
        var values = engine.Query<string>("colour(C).", "C").ToList();
        Assert.Equal(new[] { "red", "green", "blue" }, values);
    }

    [Fact]
    public void Query_TypedListResult_PerSolution()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public team/1.\n"
            + "team([alice, bob]).\n"
            + "team([carol]).\n");
        var teams = engine.Query<List<string>>("team(L).", "L").ToList();
        Assert.Equal(2, teams.Count);
        Assert.Equal(new[] { "alice", "bob" }, teams[0]);
        Assert.Equal(new[] { "carol" }, teams[1]);
    }

    [Fact]
    public void Query_AutoDetect_ZeroVariables_Throws()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<InvalidOperationException>(
            () => engine.Query<int>("true.").ToList());
        Assert.Contains("no variables", ex.Message);
    }

    [Fact]
    public void Query_AutoDetect_MultipleVariables_Throws()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- public p/2.\np(1, 2).\n");
        var ex = Assert.Throws<InvalidOperationException>(
            () => engine.Query<int>("p(X, Y).").ToList());
        Assert.Contains("multiple variables", ex.Message);
    }

    [Fact]
    public void Query_ExplicitVariable_UnknownName_Throws()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- public p/1.\np(7).\n");
        var ex = Assert.Throws<InvalidOperationException>(
            () => engine.Query<int>("p(X).", "Z").ToList());
        Assert.Contains("does not bind", ex.Message);
    }

    [Fact]
    public void QueryFirst_ReturnsFirstSolution()
    {
        var engine = new PrologEngine();
        int first = engine.QueryFirst<int>("between(10, 20, X).");
        Assert.Equal(10, first);
    }

    [Fact]
    public void QueryFirst_FailedQuery_ReturnsDefault()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- public p/1.\np(1).\n");
        // No solution: p(99) doesn't unify with any clause.
        Assert.Equal(0, engine.QueryFirst<int>("p(99), p(X).", "X"));
    }

    [Fact]
    public void QueryFirst_ExplicitName()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- public greet/2.\ngreet(hello, world).\n");
        var s = engine.QueryFirst<string>("greet(_, X).", "X");
        Assert.Equal("world", s);
    }

    [Fact]
    public void Query_UsesCustomConverter()
    {
        // Stress: typed query result projects through a user converter
        // (the chunk-238/239 path) — making sure Query<T> goes through
        // the same FromTerm<T> dispatch and not a shortcut.
        var engine = new PrologEngine();
        engine.RegisterConverter<Chunk239Tests.Point>(
            toTerm: (e, p) => throw new NotSupportedException(),
            fromTerm: t =>
            {
                var c = (Shumway.Compiler.Ast.CompoundTerm)t;
                return new Chunk239Tests.Point(
                    (int)((Shumway.Compiler.Ast.IntTerm)c.Args[0]).Value,
                    (int)((Shumway.Compiler.Ast.IntTerm)c.Args[1]).Value);
            });
        engine.ConsultString(
            ":- public pt/1.\n"
            + "pt(p(1, 2)).\n"
            + "pt(p(3, 4)).\n");
        var pts = engine.Query<Chunk239Tests.Point>("pt(P).", "P").ToList();
        Assert.Equal(2, pts.Count);
        Assert.Equal(new Chunk239Tests.Point(1, 2), pts[0]);
        Assert.Equal(new Chunk239Tests.Point(3, 4), pts[1]);
    }
}
