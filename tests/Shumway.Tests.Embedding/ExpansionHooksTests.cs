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
    public void PrologLoadContext_InsideAHook_ReportsTheLoadedFile_NotTheHooksModule()
    {
        var e = new PrologEngine();
        // A GLOBAL (user) term_expansion hook records the module it is invoked for.
        e.ConsultString(
            "term_expansion(mark(_), (:- assertz(loaded_from(M)))) :- "
            + "prolog_load_context(module, M).");
        // Load a file with its OWN module; its mark(_) triggers the hook. The hook
        // must see clientmod (the file being loaded), NOT user (the hook's module).
        e.ConsultString(":- module(clientmod, []).\nmark(here).");
        Assert.Equal("clientmod",
            Assert.IsType<AtomTerm>(e.Query("loaded_from(X).")["X"]).Name);
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
    public void PrologTermExpansion_FromAPreloadedHook_ExpandsLaterConsults()
    {
        var e = new PrologEngine();
        // A shim consulted first defines term_expansion/2; a later consult's
        // matching terms are expanded by it (the downloaded-library path — the
        // library's own term_expansion works once it is loaded).
        e.ConsultString("term_expansion(macro(X), expanded(X)).");
        e.ConsultString("macro(hello).\nplain(y).");
        Assert.True(e.Query("expanded(hello).").Success);
        Assert.True(e.Query("plain(y).").Success);
        Assert.False(e.Query("catch(macro(hello), _, fail).").Success);
    }

    [Fact]
    public void PrologTermExpansion_ReturningAList_IsSeveralClauses()
    {
        var e = new PrologEngine();
        e.ConsultString("term_expansion(pair(A,B), [first(A), second(B)]).");
        e.ConsultString("pair(one, two).");
        Assert.True(e.Query("first(one).").Success);
        Assert.True(e.Query("second(two).").Success);
    }

    [Fact]
    public void PrologGoalExpansion_RewritesBodyGoals_PreservingVariables()
    {
        var e = new PrologEngine();
        // A shim rewrites old_api(X) body goals to new_api(X) — the shared X must
        // survive the rewrite (the head's R and the rewritten goal's arg are one).
        e.ConsultString("goal_expansion(old_api(X), new_api(X)).\nnew_api(ok).");
        e.ConsultString("run(R) :- old_api(R).");
        Assert.Equal("ok", Assert.IsType<AtomTerm>(e.Query("run(R).")["R"]).Name);
    }

    [Fact]
    public void CsGoalExpansion_RewritesBodyGoals()
    {
        var e = new PrologEngine();
        // Strip log(_) goals to true.
        e.RegisterGoalExpansion(g =>
            g is CompoundTerm { Functor: "log", Args.Length: 1 } ? new AtomTerm("true") : null);
        e.ConsultString("go(X) :- log(before), X = done, log(after).");
        Assert.Equal("done", Assert.IsType<AtomTerm>(e.Query("go(X).")["X"]).Name);
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
