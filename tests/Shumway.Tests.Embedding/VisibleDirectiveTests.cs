using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 265 — Arity-Prolog <c>:- visible foo/N.</c>. Arity's "visible table" is
/// exported AND modifiable, so Shumway maps <c>visible</c> to <c>dynamic</c>: the
/// predicate is ISO-mutable (assert/retract allowed). When it is declared WITH
/// clauses (the common Arity shape), it ALSO gets a build-time WAM/IL snapshot
/// (ADR-023 priming) that runs from the first call and is evicted the instant it
/// is mutated — so the predicate compiles (its WAM/IL is dumpable) yet stays
/// mutable. <c>:- public</c> stays a truly static, immutable export.
/// </summary>
public class VisibleDirectiveTests
{
    [Fact]
    public void VisibleDirective_DeclaresMutablePredicate()
    {
        // `:- visible p/1.` with no clauses, then assertz — p/1 is dynamic.
        var engine = new PrologEngine();
        engine.ConsultString(":- visible p/1.");
        Assert.True(engine.Query("assertz(p(1)).").Success);
        Assert.True(engine.Query("assertz(p(2)).").Success);
        var sol = engine.Query("findall(X, p(X), L).");
        Assert.Equal("[1, 2]", AstTermRenderer.Render(sol["L"]!));
    }

    [Fact]
    public void VisibleDirective_WithClauses_RunsAndStaysMutable()
    {
        // The real Arity shape: `:- public X. :- visible X.` on a predicate that
        // HAS clauses. It runs (the clauses are live), AND it is ISO-mutable:
        // assert/retract succeed and are visible (logical update view).
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public q/2. :- visible q/2.\n" +
            "q(a, 1).\nq(b, 2).\n");
        Assert.True(engine.Query("q(b, X), X == 2.").Success);
        // mutable — a new clause is visible, an existing one can be retracted.
        Assert.True(engine.Query("assertz(q(c, 3)).").Success);
        Assert.True(engine.Query("q(c, X), X == 3.").Success);
        Assert.True(engine.Query("retract(q(a, 1)).").Success);
        Assert.False(engine.Query("q(a, _).").Success);
    }

    [Fact]
    public void VisibleDirective_ListForm()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- visible [a/0, b/1, c/2].");
        Assert.True(engine.Query("assertz(a).").Success);
        Assert.True(engine.Query("assertz(b(x)).").Success);
        Assert.True(engine.Query("assertz(c(1, 2)).").Success);
    }

    [Fact]
    public void VisibleDirective_GroupedCommaForm()
    {
        // `:- visible a/0, b/1.` — the GNU comma-separated form.
        var engine = new PrologEngine();
        engine.ConsultString(":- visible a/0, b/1.");
        Assert.True(engine.Query("assertz(a).").Success);
        Assert.True(engine.Query("assertz(b(7)).").Success);
    }

    [Fact]
    public void VisibleAndDynamic_AreInterchangeable()
    {
        // Both declare a mutable predicate.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic d/1. :- visible v/1.");
        Assert.True(engine.Query("assertz(d(a)).").Success);
        Assert.True(engine.Query("assertz(v(b)).").Success);
        Assert.True(engine.Query("d(a).").Success);
        Assert.True(engine.Query("v(b).").Success);
    }
}
