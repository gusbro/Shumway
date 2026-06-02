using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 265 — Arity-Prolog accepts <c>:- visible foo/N.</c> as an
/// alias for <c>:- dynamic foo/N.</c>. Shumway treats them as
/// synonyms so Arity sources compile unchanged.
/// </summary>
public class VisibleDirectiveTests
{
    [Fact]
    public void VisibleDirective_DeclaresPredicateDynamic()
    {
        // `:- visible p/1.` makes p/1 dynamic — assertz works at
        // runtime without raising permission_error(modify, static).
        var engine = new PrologEngine();
        engine.ConsultString(":- visible p/1.");
        Assert.True(engine.Query("assertz(p(1)).").Success);
        Assert.True(engine.Query("assertz(p(2)).").Success);
        var sol = engine.Query("findall(X, p(X), L).");
        Assert.Equal("[1, 2]", AstTermRenderer.Render(sol["L"]!));
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
        // Both forms in one consult; both predicates end up dynamic.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic d/1. :- visible v/1.");
        Assert.True(engine.Query("assertz(d(a)).").Success);
        Assert.True(engine.Query("assertz(v(b)).").Success);
        Assert.True(engine.Query("d(a).").Success);
        Assert.True(engine.Query("v(b).").Success);
    }
}
