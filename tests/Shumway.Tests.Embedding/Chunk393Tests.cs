using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 393 (Phase 29, Stage 9b-3 — the applied dead-region prune). `--region-prune`
/// region-compiles the bundle and skips emitting a standalone IL method for each
/// ABSORBED-ONLY predicate (reached only as a br-member of a live region). These tests
/// pin: (1) the prune analysis fires (a non-zero region-absorbed count) on a region
/// program, and (2) the pruned bundle still loads and runs correctly — the absorbed
/// members run from inside the region methods (and keep their Tier-0 WAM as a fallback).
/// The cross-process IL-path correctness is covered end-to-end on Blint via shumway-link.
/// </summary>
public class Chunk393Tests
{
    // A region program: `co/3` (+ its local closure g/p/r) is absorbed into the
    // region rooted at the public entry; only the entry stays a live root.
    private const string Source =
        ":- public run/1.\n"
        + "g(1).\n g(2).\n g(3).\n"
        + "p(a, 1).\n p(b, 2).\n p(c, 3).\n"
        + "r(K, V) :- p(K, V).\n"
        + "co(S, K, V) :- g(S), r(K, V).\n"
        + "run(L) :- findall(S-K-V, co(S, K, V), L).\n";

    private static LinkResult LinkWithPrune()
    {
        var obj = ShmoCompiler.CompileSource(Source, "user");
        return ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("run", 1) },
            RegionPrune = true,   // implies IncludeCompiledIl
            IncludeCompiledIl = true,
            // Chunk 398: the per-module "stage9_prunable" dry-run report is now opt-in
            // (the APPLIED prune moved into BundleWriter.CompileEntryToIl over the exact
            // calleeMap). Prune_FindsAbsorbedOnlyPredicates asserts on that report, so it
            // requests it; harmless for PrunedBundle_LoadsAndRunsCorrectly.
            RegionPruneReport = true,
        });
    }

    [Fact]
    public void Prune_FindsAbsorbedOnlyPredicates()
    {
        var result = LinkWithPrune();
        Assert.True(result.Success);
        var diag = result.Diagnostics.FirstOrDefault(d => d.Code == "stage9_prunable");
        Assert.NotNull(diag);
        // The message reports "<N> are region-absorbed (standalone prunable)"; N > 0
        // here (g/p/r/co are reached only as br-members of run's region).
        Assert.DoesNotContain("0 are region-absorbed", diag!.Message);
    }

    [Fact]
    public void PrunedBundle_LoadsAndRunsCorrectly()
    {
        var result = LinkWithPrune();
        Assert.True(result.Success);
        Assert.NotNull(result.Bytes);

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        // 3 (g) × 3 (p) = 9 solutions; the absorbed members produced them from inside
        // the region method (or their WAM fallback).
        Assert.True(engine.Query("run(L), length(L, 9).").Success);
        Assert.False(engine.Query("run(L), length(L, 8).").Success);
    }
}
