using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 265 — Arity-Prolog accepts <c>:- visible foo/N.</c>. Its real
/// Arity meaning is an EXPORT declaration (Arity's "visible table" — the
/// predicate is looked up by other modules / <c>call/2</c>), i.e. the same as
/// <c>:- public foo/N.</c>, NOT a mutability declaration. So a
/// <c>:- visible</c> predicate that has clauses compiles as a normal static
/// public predicate (and reaches WAM/IL); a clause-less one that is later
/// <c>assertz</c>'d still works because <c>implicit_dynamic</c> auto-promotes
/// it on first assert. (Chunk 265 originally mis-aliased <c>visible</c> to
/// <c>dynamic</c>; that peeled clause-bearing visible predicates into the
/// dynamic store, so they produced 0 static predicates — no WAM, no IL.)
/// </summary>
public class VisibleDirectiveTests
{
    [Fact]
    public void VisibleDirective_ClauselessThenAssertz_Works()
    {
        // `:- visible p/1.` with no clauses, then assertz — implicit_dynamic
        // auto-promotes p/1 on first assert, so this works exactly as before.
        var engine = new PrologEngine();
        engine.ConsultString(":- visible p/1.");
        Assert.True(engine.Query("assertz(p(1)).").Success);
        Assert.True(engine.Query("assertz(p(2)).").Success);
        var sol = engine.Query("findall(X, p(X), L).");
        Assert.Equal("[1, 2]", AstTermRenderer.Render(sol["L"]!));
    }

    [Fact]
    public void VisibleDirective_WithClauses_CompilesAsStaticPublic()
    {
        // The real Arity shape: `:- public X. :- visible X.` on a predicate
        // that HAS clauses. It must compile to a static public predicate (so
        // it reaches WAM/IL through shumway-compile), not be peeled into the
        // dynamic store. Here we assert it runs, and that it is NOT dynamic
        // (assertz raises a permission error under ISO-strict mode).
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public q/2. :- visible q/2.\n" +
            "q(a, 1).\nq(b, 2).\n");
        Assert.True(engine.Query("q(b, X), X == 2.").Success);
        // It is static, not dynamic: with implicit_dynamic off, assertz fails.
        engine.Query("set_prolog_flag(implicit_dynamic, false).");
        Assert.ThrowsAny<System.Exception>(() => engine.Query("assertz(q(c, 3)).").Success);
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
    public void VisibleAndDynamic_BothAcceptRuntimeAssert()
    {
        // d/1 is declared dynamic outright; v/1 is declared visible (public)
        // with no clauses and auto-promotes on first assertz. Both end up
        // mutable here, by different routes.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic d/1. :- visible v/1.");
        Assert.True(engine.Query("assertz(d(a)).").Success);
        Assert.True(engine.Query("assertz(v(b)).").Success);
        Assert.True(engine.Query("d(a).").Success);
        Assert.True(engine.Query("v(b).").Success);
    }
}
