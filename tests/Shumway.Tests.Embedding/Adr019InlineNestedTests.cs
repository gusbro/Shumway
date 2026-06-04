using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-019 — inline nested compound build (write) and match (read)
/// produce the same results as the BFS, end-to-end. Covers deep nesting, list
/// literals, and a repeated variable across a nested last-arg boundary (the
/// read-mode descent must reach the same slot).</summary>
public class Adr019InlineNestedTests
{
    private static PrologEngine Load(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(program);
        return engine;
    }

    [Fact]
    public void ListLiteral_BuildsCorrectly()
    {
        var engine = Load("q(X) :- X = [1, 2, 3].");
        var sol = engine.Query("q(L), L = [1, 2, 3].");
        Assert.True(sol.Success);
    }

    [Fact]
    public void DeepNesting_BuildsCorrectly()
    {
        var engine = Load("d(X) :- X = f(g(h(1))).");
        var sol = engine.Query("d(D).");
        Assert.True(sol.Success);
        Assert.Equal("f(g(h(1)))", sol["D"]!.ToString());
    }

    [Fact]
    public void NestedLastArg_HeadMatch_RepeatedVar()
    {
        // foo(X, bar(X)): the second X is inside bar, the last arg — matched
        // inline (read mode). A call binds it through the nested descent.
        var engine = Load("rep(foo(X, bar(X))).");
        var ok = engine.Query("rep(foo(1, bar(Z))), Z == 1.");
        Assert.True(ok.Success);
        var bad = engine.Query("rep(foo(1, bar(2))).");
        Assert.False(bad.Success);   // X=1 vs bar(2) mismatch
    }
}
