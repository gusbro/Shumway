using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 440 — linking MULTIPLE module-less .shmo files. Chunk 209 forced
/// ShmoCompiler's module name to "user" whenever no <c>:- module/1</c>
/// directive was present, ignoring the per-file fallback — so two
/// module-less files could never be linked together (<c>duplicate_module</c>)
/// and, had the linker allowed it, their locals would have aliased (both
/// baked <c>user$helper</c> into the bytecode). The fix restores per-file
/// module identity at compile time (the CLAUDE.md invariant: each source
/// file is one module) and makes the consumers chunk 209 was protecting
/// module-aware: dynamic-seed rehydration rewrites under the entry's module
/// name + that module's locals (<c>_dynamicSeedModule</c>), source-bearing
/// bundle entries consult under the entry's module name, and the bundle
/// local-fid feed was already keyed per module.
/// </summary>
public class Chunk440Tests
{
    // The acid test: both files define a LOCAL helper/1 with DIFFERENT
    // clauses. After the fix each file's caller must resolve to ITS OWN
    // helper. a's main/0 also calls b's public other/0 cross-file.
    private const string SourceA =
        ":- public main/0.\n"
        + "helper(1).\n"
        + "main :- helper(X), X == 1, other.\n";

    private const string SourceB =
        ":- public other/0.\n"
        + "helper(2).\n"
        + "other :- helper(X), X == 2.\n";

    [Fact]
    public void TwoModuleLessFiles_Link_NoDuplicateModuleError()
    {
        var result = ShmoLinker.LinkFromSources(
            new[] { ("a", SourceA), ("b", SourceB) },
            new[] { new PredicateRef("main", 0) });
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "duplicate_module");
        Assert.Contains("a", result.ReachedModules);
        Assert.Contains("b", result.ReachedModules);
    }

    [Fact]
    public void TwoModuleLessFiles_SameNameLocals_ResolvePerFile()
    {
        var result = ShmoLinker.LinkFromSources(
            new[] { ("a", SourceA), ("b", SourceB) },
            new[] { new PredicateRef("main", 0) });
        Assert.True(result.Success);

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));

        // main succeeds only if a's main sees helper(1) AND b's other sees
        // helper(2) — aliased locals would make one of the X == N guards fail.
        Assert.True(engine.Query("main.").Success);
        Assert.True(engine.Query("other.").Success);

        // Locals stay local: helper/1 is not callable from the top level.
        var ex = Assert.ThrowsAny<System.Exception>(
            () => engine.Query("helper(_)."));
        Assert.Contains("existence_error", ex.Message);
    }

    [Fact]
    public void ModuleLessFiles_CompileAndLinkApi_PerFileLocals()
    {
        // Same shape through the explicit compile + LinkConfig API (what
        // shumway-compile / shumway-link do per file).
        var objA = ShmoCompiler.CompileSource(SourceA, "a");
        var objB = ShmoCompiler.CompileSource(SourceB, "b");
        Assert.Equal("a", objA.ModuleName);
        Assert.Equal("b", objB.ModuleName);

        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { objA, objB },
            EntryPoints = new[] { new PredicateRef("main", 0) },
        });
        Assert.True(result.Success);

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        Assert.True(engine.Query("main.").Success);
    }

    [Fact]
    public void ModuleDirective_StillWins_OverFallback()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- module(mymod).\n:- public p/0.\np.\n", "filename");
        Assert.Equal("mymod", obj.ModuleName);
    }

    [Fact]
    public void CompileSource_DefaultFallback_StaysUser()
    {
        // In-memory consult-style compiles (no file) keep "user" — only
        // the FILE path changed (per-file fallback = base name).
        var obj = ShmoCompiler.CompileSource(":- public p/0.\np.\n");
        Assert.Equal("user", obj.ModuleName);
        var empty = ShmoCompiler.CompileSource(":- public p/0.\np.\n", "");
        Assert.Equal("user", empty.ModuleName);
    }

    [Fact]
    public void SameFallbackName_TwoObjects_ErrorsClearly()
    {
        // Two module-less files with the same base name (different
        // directories) still collide — the module name is baked into the
        // bytecode's local mangling at compile time, so the linker cannot
        // rename one. The diagnostic must say how to fix it.
        var result = ShmoLinker.LinkFromSources(
            new[] { ("dup", SourceA), ("dup", SourceB) },
            new[] { new PredicateRef("main", 0) });
        Assert.False(result.Success);
        var diag = Assert.Single(result.Diagnostics,
            d => d.Code == "duplicate_module");
        Assert.Contains(":- module", diag.Message);
    }

    [Fact]
    public void ExplicitDuplicateModuleNames_StillError()
    {
        var result = ShmoLinker.LinkFromSources(
            new[]
            {
                ("a", ":- module(m).\n:- public p/0.\np.\n"),
                ("b", ":- module(m).\n:- public q/0.\nq.\n"),
            },
            new[] { new PredicateRef("p", 0) });
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "duplicate_module");
    }

    // ------------------------------------------------------------------
    // The chunk-209 scenario the "user" forcing was protecting: dynamic
    // predicates with source clauses must dispatch from a bundle — now
    // under a per-file module name. d/1's second clause calls the LOCAL
    // gen/1, so the rehydrated dynamic clause must be rewritten under the
    // SAME module context the static bytecode was mangled with
    // (dynfile$gen), or dispatch dies with existence_error.
    // ------------------------------------------------------------------
    private const string DynSource =
        ":- public boot/0.\n"
        + ":- dynamic d/1.\n"
        + "d(seed).\n"
        + "d(X) :- gen(X).\n"
        + "gen(99).\n"
        + "boot :- assertz(d(7)), d(seed), d(7), d(99),\n"
        + "        retract(d(7)), \\+ d(7).\n";

    [Fact]
    public void ModuleLessFile_DynamicSeeds_RunFromBundle()
    {
        var result = ShmoLinker.LinkFromSources(
            new[] { ("dynfile", DynSource) },
            new[] { new PredicateRef("boot", 0) });
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics));

        // Fresh engine = the cross-process shape (LoadBundle from bytes,
        // source stripped by the release compile).
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        Assert.True(engine.Query("boot.").Success);
        // The seeded clauses are live dynamic clauses: visible and
        // retractable from a follow-up query.
        Assert.True(engine.Query("d(seed).").Success);
        Assert.True(engine.Query("d(99).").Success);  // via dynfile$gen
        Assert.True(engine.Query("retract(d(seed)), \\+ d(seed).").Success);
    }

    [Fact]
    public void TwoModuleLessFiles_WithDynamicSeeds_DoNotAlias()
    {
        // Each file owns a dynamic d/1 fed by ITS OWN local gen/1 — wait,
        // dynamic predicates live in a flat global namespace, so two files
        // may not both declare d/1 with clauses without sharing it. Use
        // distinct dynamic names; the per-file part is each body's call to
        // its own LOCAL gen/1.
        const string dynA =
            ":- public boot_a/0.\n:- dynamic da/1.\n"
            + "da(X) :- gen(X).\ngen(1).\nboot_a :- da(1).\n";
        const string dynB =
            ":- public boot_b/0.\n:- dynamic db/1.\n"
            + "db(X) :- gen(X).\ngen(2).\nboot_b :- db(2).\n";
        var result = ShmoLinker.LinkFromSources(
            new[] { ("da_file", dynA), ("db_file", dynB) },
            new[] { new PredicateRef("boot_a", 0), new PredicateRef("boot_b", 0) });
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics));

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        Assert.True(engine.Query("boot_a.").Success);
        Assert.True(engine.Query("boot_b.").Success);
        Assert.False(engine.Query("da(2).").Success);
        Assert.False(engine.Query("db(1).").Success);
    }

    // ------------------------------------------------------------------
    // Source-bearing entries (Debug compile, no --strip): LoadBundle
    // consults each entry's source under the entry's module name, so two
    // module-less files keep their per-file locals instead of merging
    // into a rolling "user" module.
    // ------------------------------------------------------------------
    [Fact]
    public void DebugBundle_SourceBearingEntries_KeepPerFileLocals()
    {
        var objA = ShmoCompiler.CompileSource(SourceA, "a", ShmoBuildMode.Debug);
        var objB = ShmoCompiler.CompileSource(SourceB, "b", ShmoBuildMode.Debug);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { objA, objB },
            EntryPoints = new[] { new PredicateRef("main", 0) },
        });
        Assert.True(result.Success);

        var bundle = BundleReader.FromBytes(result.Bytes!);
        Assert.Contains(bundle.Entries, e => !string.IsNullOrEmpty(e.Source));

        var engine = new PrologEngine();
        engine.LoadBundle(bundle);
        Assert.True(engine.Query("main.").Success);
        Assert.True(engine.Query("other.").Success);
    }
}
