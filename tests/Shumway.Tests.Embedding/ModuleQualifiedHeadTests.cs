using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Module-qualified clause heads for the global expansion hooks: real libraries
/// (Scryer's atts.pl, dcgs.pl) install `user:term_expansion/2` and
/// `user:goal_expansion/2` with a `user:` head so the hook applies to every later
/// consult. These are the ONLY module-qualified clause heads that occur — we strip
/// the `M:` and keep the clause in its own file's module, so the hook functor stays
/// global while the clause body still resolves against that module's predicates.
/// </summary>
public class ModuleQualifiedHeadTests
{
    [Fact]
    public void UserQualifiedTermExpansion_InstallsAGlobalHook()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- module(mymacros, [foo/1]).\n" +
            "user:term_expansion(mark(X), expanded(X)).");
        e.ConsultString("mark(hi).");
        Assert.True(e.Query("expanded(hi).").Success);
    }

    [Fact]
    public void HookBody_ResolvesInItsOwnModulesPredicates()
    {
        // The dcgs.pl regression: an export-qualified module installs
        // `user:term_expansion` whose body calls a module-LOCAL helper. The head
        // is the global hook, but the body's `helper/2` must resolve to this
        // module's (mangled) helper — not to `user` where it doesn't exist.
        var e = new PrologEngine();
        e.ConsultString(
            ":- module(macrolib, [foo/1]).\n" +
            "user:term_expansion(src(X), Out) :- helper(X, Out).\n" +
            "helper(X, dst(X)).");
        // A term the hook does NOT match must pass through unchanged (the hook
        // body simply fails, it must not error or drop the clause).
        e.ConsultString("plain_fact(kept).\nsrc_holder(src(7)).");
        Assert.True(e.Query("plain_fact(kept).").Success);
    }

    [Fact]
    public void NonMatchingHook_LeavesOtherFilesClausesIntact()
    {
        // Guards the corruption class: a defined-but-non-matching term_expansion
        // must not make a subsequently consulted file's clauses vanish.
        var e = new PrologEngine();
        e.ConsultString(
            ":- module(m, []).\n" +
            "user:term_expansion(special(X), handled(X)).");
        e.ConsultString("aaa(1).\nbbb(2).");
        Assert.True(e.Query("aaa(1).").Success);
        Assert.True(e.Query("bbb(2).").Success);
    }
}
