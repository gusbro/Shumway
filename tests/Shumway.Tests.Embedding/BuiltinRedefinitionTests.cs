using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Redefining a procedure of the processor. ISO 7.5.2 makes every
/// built-in predicate static; assertz/1 already refused them, but a CLAUSE
/// in a consulted file shadowed the builtin silently — `write(hello).` in a
/// plain file left the engine unable to write, with no diagnostic. And a
/// bundle-booted engine (WebShumway, every --exe) let assertz through as
/// well: a linked bundle's predicates are bytecode with no clauses in any
/// manifest, so the guard's clause scan never saw them.</summary>
public class BuiltinRedefinitionTests
{
    // ---- consult-time clauses, global module ----

    [Fact]
    public void AClauseForARegistryBuiltin_IsDroppedWithAWarning()
    {
        var warnings = new StringWriter();
        var outw = new StringWriter();
        // Out is wired before the first query — the stream registry keeps
        // the writer it was handed at setup.
        var e = new PrologEngine { Warnings = warnings, Out = outw };
        e.ConsultString("write(hello) :- true.\nmine(ok).\n");
        Assert.Contains("(write)/1", warnings.ToString());
        Assert.True(e.Query("mine(ok).").Success);
        // write/1 still works — the point of refusing.
        Assert.True(e.Query("write(check).").Success);
        Assert.Contains("check", outw.ToString());
    }

    [Fact]
    public void AClauseForALibraryPredicate_ShadowsIt_AsEveryTutorialExpects()
    {
        // The prelude's Prolog-defined predicates are deliberately NOT
        // protected at consult time: defining append/3 or member/2 in a
        // plain file is ordinary Prolog, and the user's definition wins —
        // the SWI line between locked system predicates and redefinable
        // library ones. assertz/1 still refuses them (the test below).
        var e = new PrologEngine { Warnings = new StringWriter() };
        e.ConsultString("append(mine).\n");
        Assert.True(e.Query("append(mine).").Success);
    }

    [Fact]
    public void AssertzOnALoadedLibraryPredicate_StillRefused()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(assertz(length(a, 1)), error(permission_error(modify, static_procedure, length/2), _), true).").Success);
        Assert.True(e.Query("length([a, b], N), N == 2.").Success);
    }

    [Fact]
    public void ANamedModulesClause_ShadowsLocally_AsBefore()
    {
        // ADR-008: a module-local definition shadows the builtin INSIDE the
        // module; the rest of the program keeps the real one.
        var e = new PrologEngine { Warnings = new StringWriter() };
        e.ConsultString("""
            :- module(shadower).
            :- public probe/1.
            length(_, 42).
            probe(N) :- length(x, N).
            """);
        Assert.True(e.Query("probe(42).").Success);
        Assert.True(e.Query("length([a], N), N == 1.").Success);
    }

    [Fact]
    public void TheGlobalHooks_AreStillUserDefinable()
    {
        var e = new PrologEngine { Warnings = new StringWriter() };
        e.ConsultString("""
            term_expansion(magic, expanded_fact).
            magic.
            """);
        Assert.True(e.Query("expanded_fact.").Success);
    }

    // ---- assertz on a bundle-booted engine (the WebShumway shape) ----

    private static PrologEngine BundleBooted()
    {
        var bundle = new Bundle(new[]
        {
            new BundleEntry("user", ":- public greet/1.\ngreet(world).\n"),
        });
        return PrologEngine.FromBundle(BundleReader.FromBytes(
            BundleWriter.ToBytes(bundle, includeCompiledBytecode: true)));
    }

    [Fact]
    public void AssertzOnAPreludePredicate_RefusedOnABundleBootToo()
    {
        // The linked stdlib carries the prelude as bytecode: no clauses, no
        // registry entry for a Prolog-defined predicate like length/2 — the
        // precompiled-predicate map is what the guard must consult.
        var e = BundleBooted();
        Assert.True(e.Query(
            "catch(assertz(length(a, 1)), error(permission_error(modify, static_procedure, length/2), _), true).").Success);
        Assert.True(e.Query("length([x, y], N), N == 2.").Success);
    }

    [Fact]
    public void AssertzOnABundlesOwnStatic_Refused()
    {
        var e = BundleBooted();
        Assert.True(e.Query(
            "catch(assertz(greet(mars)), error(permission_error(modify, static_procedure, greet/1), _), true).").Success);
        Assert.True(e.Query("findall(X, greet(X), [world]).").Success);
    }

    [Fact]
    public void AssertzOnAFreshPredicate_StillAutoPromotes()
    {
        // implicit_dynamic must keep working on a bundle boot: an undefined
        // name auto-promotes on first assert, exactly as on a live engine.
        var e = BundleBooted();
        Assert.True(e.Query("assertz(scratch(1)), scratch(1).").Success);
    }
}
