using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 30 chunk 441 — Arity implicit-dynamic semantics for META-CALLED
/// undeclared predicates at link time.
///
/// <para>Under Arity Prolog a meta-call to an undeclared fact predicate is
/// valid: it fails when nothing was asserted and works after an
/// <c>assertz</c>. Pre-441 the linker errored
/// (<c>missing_predicate</c>) on <c>call(und_fact(X))</c> when no
/// <c>:- dynamic und_fact/N</c> existed anywhere. Now: every call-graph
/// edge carries a DIRECT/META marker (<see cref="ShmoCallEdge"/>,
/// computed module-wide per target on the PRE-MetaTransform bodies —
/// the transform erases the meta wrappers); when EVERY unresolved
/// reference to a target is a META edge from an arity-compiled module,
/// the linker registers the target as an implicit EMPTY DYNAMIC
/// predicate (exactly as a clauseless <c>:- dynamic</c> declaration
/// would) and emits an <c>arity_implicit_dynamic</c> INFO diagnostic. A
/// DIRECT body goal to an undefined predicate stays a hard linker error
/// — arity mode or not.</para>
/// </summary>
public class Chunk441Tests
{
    private static ShmoObject CompileArity(string source, string moduleName)
    {
        var res = ShmoCompiler.TryCompileSource(
            source, moduleName, ShmoBuildMode.Release, arityCompat: true);
        Assert.True(res.Success, string.Join("; ",
            res.Errors.Select(e => e.Message)));
        return res.Object!;
    }

    // ------------------------------------------------------------------
    // (a) The repro: meta-call to an undeclared fact predicate in an
    //     arity module links WITHOUT --allow-undefined, fails cleanly at
    //     runtime, and works after assertz.
    // ------------------------------------------------------------------
    private const string MetaSource =
        ":- public go/1.\n"
        + ":- public go2/0.\n"
        + "go(X) :- call(und_fact(X)).\n"
        + "go2 :- assertz(und_fact(7)).\n";

    [Fact]
    public void ArityModule_MetaCallToUndeclared_LinksAsEmptyDynamic()
    {
        var obj = CompileArity(MetaSource, "metamod");
        Assert.True(obj.ArityCompat);

        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("go", 1), new PredicateRef("go2", 0) },
            // NO AllowUndefined — the link must succeed on its own.
        });
        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "missing_predicate");
        var info = Assert.Single(result.Diagnostics,
            d => d.Code == "arity_implicit_dynamic");
        Assert.Equal(LinkSeverity.Info, info.Severity);
        Assert.Contains("und_fact/1", info.Message);
        Assert.Contains("metamod", info.Message);

        // Runtime: the empty dynamic fails cleanly (no existence_error),
        // then assertz populates it and the meta-call sees the fact.
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        Assert.False(engine.Query("go(_).").Success);
        Assert.True(engine.Query("go2.").Success);
        var sol = engine.Query("go(X).");
        Assert.True(sol.Success);
        Assert.Equal(7L, ((IntTerm)sol["X"]!).Value);
    }

    // ------------------------------------------------------------------
    // (b) A DIRECT body goal to an undefined predicate stays a linker
    //     ERROR even in an arity module.
    // ------------------------------------------------------------------
    [Fact]
    public void ArityModule_DirectCallToUndefined_StillLinkerError()
    {
        var obj = CompileArity(
            ":- public go3/0.\ngo3 :- pepe.\n", "directmod");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("go3", 0) },
        });
        Assert.False(result.Success);
        var diag = Assert.Single(result.Diagnostics,
            d => d.Code == "missing_predicate");
        Assert.Equal(LinkSeverity.Error, diag.Severity);
        Assert.Contains("pepe/0", diag.Message);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Code == "arity_implicit_dynamic");
    }

    // ------------------------------------------------------------------
    // (b2) Mixed: the same target referenced BOTH meta and direct inside
    //      an arity module — one direct reference anywhere poisons the
    //      implicit-dynamic treatment (module-wide marking).
    // ------------------------------------------------------------------
    [Fact]
    public void ArityModule_MixedMetaAndDirect_StillLinkerError()
    {
        var obj = CompileArity(
            ":- public go/1.\n"
            + "go(X) :- call(und(X)).\n"
            + "go(X) :- und(X).\n", "mixedmod");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("go", 1) },
        });
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "missing_predicate");
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Code == "arity_implicit_dynamic");
    }

    // ------------------------------------------------------------------
    // (c) A NON-arity module with the same meta-call shape keeps today's
    //     missing_predicate error.
    // ------------------------------------------------------------------
    [Fact]
    public void NonArityModule_MetaCallToUndeclared_StillLinkerError()
    {
        var res = ShmoCompiler.TryCompileSource(
            MetaSource, "plainmod", ShmoBuildMode.Release);   // no --arity
        Assert.True(res.Success);
        Assert.False(res.Object!.ArityCompat);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { res.Object! },
            EntryPoints = new[] { new PredicateRef("go", 1), new PredicateRef("go2", 0) },
        });
        Assert.False(result.Success);
        var diag = Assert.Single(result.Diagnostics,
            d => d.Code == "missing_predicate");
        Assert.Contains("und_fact/1", diag.Message);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Code == "arity_implicit_dynamic");
    }

    // ------------------------------------------------------------------
    // (d) Two arity modules meta-calling the SAME undeclared target:
    //     links, exactly one registration.
    // ------------------------------------------------------------------
    [Fact]
    public void TwoArityModules_SameUndeclaredTarget_SingleRegistration()
    {
        var objA = CompileArity(
            ":- public enter_a/1.\n"
            + "enter_a(X) :- call(shared_und(X)).\n", "arity_a");
        var objB = CompileArity(
            ":- public enter_b/1.\n"
            + "enter_b(X) :- findall(Y, shared_und(Y), [X]).\n", "arity_b");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { objA, objB },
            EntryPoints = new[]
            {
                new PredicateRef("enter_a", 1),
                new PredicateRef("enter_b", 1),
            },
        });
        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
        // Exactly ONE implicit-dynamic registration for shared_und/1.
        Assert.Single(result.Diagnostics, d => d.Code == "arity_implicit_dynamic");

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        Assert.False(engine.Query("enter_a(_).").Success);
        Assert.False(engine.Query("enter_b(_).").Success);
        Assert.True(engine.Query("assertz(shared_und(3)).").Success);
        Assert.True(engine.Query("enter_a(3).").Success);
        Assert.True(engine.Query("enter_b(3).").Success);
    }

    // ------------------------------------------------------------------
    // Declared dynamic in ANOTHER module + meta-referenced in an arity
    // module: normal resolution, no implicit registration.
    // ------------------------------------------------------------------
    [Fact]
    public void DeclaredDynamicElsewhere_ResolvesNormally_NoImplicit()
    {
        var objA = CompileArity(
            ":- public enter/1.\nenter(X) :- call(decl_dyn(X)).\n", "arity_user");
        var objB = ShmoCompiler.CompileSource(
            ":- dynamic decl_dyn/1.\ndecl_dyn(1).\n", "dyn_owner");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { objA, objB },
            EntryPoints = new[] { new PredicateRef("enter", 1) },
        });
        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Code == "arity_implicit_dynamic");

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        Assert.True(engine.Query("enter(1).").Success);
    }

    // ------------------------------------------------------------------
    // The meta-builtin goal-argument family also qualifies: findall / \+
    // over an undeclared fact store is the classic Arity idiom.
    // ------------------------------------------------------------------
    [Fact]
    public void ArityModule_FindallAndNegation_OverUndeclared_Link()
    {
        var obj = CompileArity(
            ":- public scan/1.\n"
            + ":- public clear/0.\n"
            + "scan(L) :- findall(X, und_store(X), L).\n"
            + "clear :- \\+ und_store(_).\n", "metafam");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[]
            {
                new PredicateRef("scan", 1),
                new PredicateRef("clear", 0),
            },
        });
        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
        Assert.Single(result.Diagnostics, d => d.Code == "arity_implicit_dynamic");

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        // Empty store: findall gives [], negation succeeds.
        Assert.True(engine.Query("scan([]).").Success);
        Assert.True(engine.Query("clear.").Success);
        Assert.True(engine.Query("assertz(und_store(a)).").Success);
        Assert.True(engine.Query("scan([a]).").Success);
        Assert.False(engine.Query("clear.").Success);
    }

    // ------------------------------------------------------------------
    // Format plumbing: ArityCompat + the per-edge META marker survive the
    // .shmo write/read round trip.
    // ------------------------------------------------------------------
    [Fact]
    public void ShmoRoundTrip_PreservesArityCompatAndMetaMarker()
    {
        var obj = CompileArity(MetaSource, "rt");
        byte[] bytes = ShmoWriter.ToBytes(obj);
        var restored = ShmoReader.FromBytes(bytes);
        Assert.True(restored.ArityCompat);
        var goEdges = restored.CallGraph[new PredicateRef("go", 1)];
        var undEdge = Assert.Single(goEdges,
            e => e.Target == new PredicateRef("und_fact", 1));
        Assert.True(undEdge.IsMeta);
    }

    [Fact]
    public void InFileFlagFlip_MarksObjectArityCompat()
    {
        // No --arity pre-enable; the in-file flag flip suffices ("ever
        // on during the compile").
        var res = ShmoCompiler.TryCompileSource(
            ":- set_prolog_flag(arity_compat, true).\n"
            + ":- public p/0.\np.\n", "flipped", ShmoBuildMode.Release);
        Assert.True(res.Success, string.Join("; ",
            res.Errors.Select(e => e.Message)));
        Assert.True(res.Object!.ArityCompat);
    }
}
