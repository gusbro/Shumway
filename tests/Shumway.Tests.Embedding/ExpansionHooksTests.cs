using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// term_expansion / goal_expansion hook interface (the shim mechanism for foreign
/// engines' libraries) + prolog_load_context/2 (how a /2 hook reads the module it
/// is expanding for — the module is not a hook argument).
/// </summary>
public class ExpansionHooksTests
{
    [Fact]
    public void PrologLoadContext_ReportsTheModuleBeingLoaded()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- module(m1, [dummy/0]).\n" +
            ":- prolog_load_context(module, X), assertz(loaded_module(X)).\n" +
            "dummy.\n");
        Assert.Equal("m1", Assert.IsType<AtomTerm>(e.Query("loaded_module(X).")["X"]).Name);
    }

    [Fact]
    public void PrologLoadContext_DefaultsToUserForAModulelessFile()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- prolog_load_context(module, X), assertz(lm(X)).\n" +
            "p.\n");
        Assert.Equal("user", Assert.IsType<AtomTerm>(e.Query("lm(X).")["X"]).Name);
    }

    [Fact]
    public void PrologLoadContext_FailsOutsideAConsult()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("prolog_load_context(module, _).").Success);
    }

    [Fact]
    public void CsTermExpansion_RewritesAFactIntoSeveralClauses()
    {
        var e = new PrologEngine();
        // Expand each `defcolor(C)` into `color(C)` + `is_color(C)`, dropping the
        // original — the shape a shim uses to lower foreign declarations.
        e.RegisterTermExpansion(term =>
            term is CompoundTerm { Functor: "defcolor", Args: [var c] }
                ? new Term[]
                {
                    new CompoundTerm("color", new[] { c }),
                    new CompoundTerm("is_color", new[] { c }),
                }
                : null);
        // One defcolor → two DISTINCT predicates (contiguous); a second defcolor
        // would interleave color/is_color (our engine enforces contiguity), which
        // real term_expansion avoids by grouping — so keep one here.
        e.ConsultString("defcolor(red).\nplain(x).");
        Assert.True(e.Query("color(red).").Success);
        Assert.True(e.Query("is_color(red).").Success);
        Assert.True(e.Query("plain(x).").Success);                    // untouched
        Assert.False(e.Query("catch(defcolor(red), _, fail).").Success); // original gone
    }

    [Fact]
    public void CsTermExpansion_CanExpandADirective()
    {
        var e = new PrologEngine();
        // The atts.pl shape: a hook catches a `:- Directive` term and lowers it to
        // clauses (there, `:- attribute …` → attribute machinery).
        e.RegisterTermExpansion(term =>
            term is CompoundTerm
            {
                Functor: ":-",
                Args: [CompoundTerm { Functor: "register", Args: [var x] }]
            }
                ? new Term[] { new CompoundTerm("registered", new[] { x }) }
                : null);
        e.ConsultString(":- register(foo).\n:- register(bar).");
        Assert.True(e.Query("registered(foo).").Success);
        Assert.True(e.Query("registered(bar).").Success);
    }
}
