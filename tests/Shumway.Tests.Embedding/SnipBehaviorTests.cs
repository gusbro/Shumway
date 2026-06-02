using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// End-to-end semantics of Arity-Prolog snips (<c>[! G !]</c>).
/// The parser desugars to <c>once((G))</c>, so these tests verify the
/// observed control-flow matches: internal backtracking is allowed
/// while searching for <c>G</c>'s first solution; once it exits, the
/// choice points it created are pruned, and a later failure does not
/// re-enter the snip.
/// </summary>
public class SnipBehaviorTests
{
    [Fact]
    public void Findall_OverSnip_CommitsToFirstSolution()
    {
        // p has three answers, but a snip wraps the call → only the
        // first reaches findall.
        var engine = new PrologEngine();
        engine.ConsultString("p(1). p(2). p(3).");
        var sol = engine.Query("findall(X, [! p(X) !], L).");
        Assert.True(sol.Success);
        var l = sol["L"];
        Assert.Equal("[1]", AstTermRenderer.Render(l!));
    }

    [Fact]
    public void OuterBacktrack_StillRunsAcrossAllOuterSolutions()
    {
        // The snip prunes only its own choice points. The outer p(X)
        // can still backtrack across all three solutions; each time
        // the snip is re-entered fresh.
        var engine = new PrologEngine();
        engine.ConsultString("p(1). p(2). p(3).");
        var sol = engine.Query("findall(X, (p(X), [! Y = a !]), L).");
        Assert.True(sol.Success);
        var l = sol["L"];
        Assert.Equal("[1, 2, 3]", AstTermRenderer.Render(l!));
    }

    [Fact]
    public void CutInside_Snip_ScopedToSnipBoundary()
    {
        // The `!` inside the snip cuts only to the snip boundary —
        // it must not cut the outer p(X) backtracking. We expect
        // both X=1 and X=2 to reach findall, each pinned to Y=a
        // (q's backtracking IS cut by the inner !).
        var engine = new PrologEngine();
        engine.ConsultString("p(1). p(2). q(a). q(b).");
        var sol = engine.Query("findall(X-Y, (p(X), [! q(Y), ! !]), L).");
        Assert.True(sol.Success);
        var l = sol["L"];
        Assert.Equal("[1-a, 2-a]", AstTermRenderer.Render(l!));
    }

    [Fact]
    public void FailingSnipBody_FailsTheSnip()
    {
        // If the snip body can't find any solution, the snip fails
        // and the surrounding goal sees the failure normally.
        var engine = new PrologEngine();
        Assert.False(engine.Query("[! fail !].").Success);
    }

    [Fact]
    public void Nested_Snip_InnerCommitsBeforeOuterContinues()
    {
        // q has two answers (a, b). The inner snip commits to Y=a.
        // Then the outer continuation forces X=1; if X=2, the outer
        // continuation fails, the inner snip's CPs are pruned, so
        // there's no second chance — outer snip fails for X=2.
        var engine = new PrologEngine();
        engine.ConsultString("p(1). p(2). q(a). q(b).");
        var sol = engine.Query(
            "findall(X-Y, (p(X), [! [! q(Y) !], X = 1 !]), L).");
        Assert.True(sol.Success);
        var l = sol["L"];
        Assert.Equal("[1-a]", AstTermRenderer.Render(l!));
    }
}
