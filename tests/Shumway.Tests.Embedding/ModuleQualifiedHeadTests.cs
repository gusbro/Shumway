using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Module-qualified clause heads (`M:Head :- Body`): a clause defines its
/// predicate in module M rather than the file's module — how Scryer's atts.pl
/// installs its GLOBAL hooks (`user:term_expansion/2`, `user:goal_expansion/2`).
/// </summary>
public class ModuleQualifiedHeadTests
{
    [Fact]
    public void UserQualifiedFact_IsCallableGlobally()
    {
        var e = new PrologEngine();
        // Inside a module file, user:greet/1 defines greet in the user module.
        e.ConsultString(":- module(somelib, []).\nuser:greet(hello).");
        Assert.True(e.Query("greet(hello).").Success);
    }

    [Fact]
    public void UserQualifiedRule_IsCallableGlobally()
    {
        var e = new PrologEngine();
        e.ConsultString(":- module(somelib, []).\nuser:calc(X) :- X is 40 + 2.");
        Assert.Equal(42L, Assert.IsType<IntTerm>(e.Query("calc(X).")["X"]).Value);
    }

    [Fact]
    public void UserQualifiedTermExpansion_InstallsAGlobalHook()
    {
        var e = new PrologEngine();
        // The atts.pl shape: a module file installs user:term_expansion/2, which
        // then expands a LATER consult's matching terms.
        e.ConsultString(
            ":- module(mymacros, []).\n" +
            "user:term_expansion(mark(X), expanded(X)).");
        e.ConsultString("mark(hi).");
        Assert.True(e.Query("expanded(hi).").Success);
    }
}
