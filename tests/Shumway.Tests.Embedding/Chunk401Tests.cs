using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 401 (Phase 29) — regression guard for the --region-prune + --strip-wam
/// unsoundness that shipped in chunks 398/400 and broke Blint's argv-driven `main`
/// path (the chunk-398/399 validation only exercised `test/0`, a DIFFERENT entry whose
/// prune keeps a different set, so it missed it). Two distinct failures, both from
/// stripping a predicate the region path still reaches BY FID:
///   1. an ABSORBED-ONLY predicate meta-called by name (e.g. via <c>catch/3</c>) had no
///      standalone form once its WAM was gone → <c>existence_error</c>;
///   2. a region's CROSS-REGION trampoline to a stripped STANDALONE-IL predicate landed
///      on a resume-marker address the interpreter fetched as a PC → <c>startPc … is
///      outside …</c>.
/// The fix: --strip-wam is a NO-OP under --region-prune (the WAM stays as the Tier-0
/// fallback; region-prune still drops the absorbed-only standalone IL for its size win).
/// These tests build a region program reached THROUGH a meta-call and run it end-to-end —
/// exactly the shape the entry-only Blint check missed.
/// </summary>
public class Chunk401Tests
{
    // classify/2 is multi-clause (region / absorbed); describe/2 calls it; safe_describe/2
    // reaches describe/2 BY FID through catch/3 (a meta-call); run/2 is the public entry.
    private const string Source =
        ":- public run/2.\n"
        + "classify(0, zero).\n"
        + "classify(N, pos) :- N > 0.\n"
        + "classify(N, neg) :- N < 0.\n"
        + "describe(N, D) :- classify(N, D).\n"
        + "safe_describe(N, D) :- catch(describe(N, D), _, D = error).\n"
        + "run(N, D) :- safe_describe(N, D).\n";

    private static LinkResult Link(bool regionPrune, bool stripWam)
    {
        var obj = ShmoCompiler.CompileSource(Source, "user");
        return ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("run", 2) },
            IncludeCompiledIl = true,
            RegionPrune = regionPrune,
            StripWam = stripWam,
        });
    }

    [Theory]
    [InlineData(false, false)]   // plain IL
    [InlineData(true, false)]    // region-prune, no strip
    [InlineData(true, true)]     // region-prune + strip (strip must be a safe no-op)
    [InlineData(false, true)]    // strip without regions (the safe standalone-IL strip)
    public void MetaCalledRegionPredicate_RunsCorrectly(bool regionPrune, bool stripWam)
    {
        var result = Link(regionPrune, stripWam);
        Assert.True(result.Success);

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));

        // describe/2 is reached only through catch/3 (by fid) — the path that broke when
        // the strip removed its standalone form. All three classify clauses must resolve.
        Assert.True(engine.Query("run(5, D), D == pos.").Success);
        Assert.True(engine.Query("run(-3, D), D == neg.").Success);
        Assert.True(engine.Query("run(0, D), D == zero.").Success);
        Assert.False(engine.Query("run(5, D), D == neg.").Success);
    }

    // Chunk 402 re-enabled the strip under regions (member-entry cursors make every
    // absorbed member fid-resolvable INTO its region, so dropping its WAM is sound).
    // The guard flips: the strip must now actually REMOVE WAM bodies — while the Theory
    // above proves the meta-called path still runs. If the aliases ever regress, the
    // Theory catches the runtime break; this catches the strip silently no-op'ing.
    [Fact]
    public void RegionPrune_StripWam_RemovesWam_AndStaysCallable()
    {
        var withStrip = Link(regionPrune: true, stripWam: true);
        var noStrip = Link(regionPrune: true, stripWam: false);
        Assert.True(withStrip.Success && noStrip.Success);

        int Preds(LinkResult r)
        {
            var entry = BundleReader.FromBytes(r.Bytes!).Entries
                .First(e => e.CompiledBytecode is { Length: > 0 });
            return CompiledModuleCodec.Decode(entry.CompiledBytecode!).Predicates.Count;
        }
        Assert.True(Preds(withStrip) < Preds(noStrip),
            $"strip should drop WAM bodies (with={Preds(withStrip)}, without={Preds(noStrip)})");
    }
}
