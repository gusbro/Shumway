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
}
