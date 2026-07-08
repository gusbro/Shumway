using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 — regression pin for the region-prune analysis↔emit scope bug
/// surfaced by the Blint <c>--exe</c> chain (ShumBlintILO mass parse
/// failures / ShumBlint infinite loop).
///
/// The persisted-bundle prune runs per ENTRY over the bundle-wide call
/// graph. Region membership is scoped to the entry being emitted
/// (<c>RegionMemberScopeFids</c>), but the ANALYSIS used to let a root
/// emitted by ANOTHER entry (the user module's <c>main/0</c> during the
/// baked $prelude entry's prune) absorb THIS entry's predicates —
/// classifying a prelude predicate reached only from user code
/// (<c>sum_list/2</c>, <c>atomic_list_concat/2</c>) as an absorbed-only
/// region member. Its standalone IL was skipped, its WAM stripped, and no
/// member-entry alias existed (the other entry's emit never actually
/// absorbs it) → <c>existence_error</c> in the shipped bundle.
///
/// The test builds the exact shape: a user module whose <c>main/0</c>
/// pulls its ITE guards into a region and calls multi-clause prelude
/// predicates cross-entry, linked with baked prelude + persisted IL +
/// region prune + WAM strip, then loaded via <c>PrologEngine.FromBundle</c>
/// (the <c>--exe</c> startup path — a plain REPL LoadBundle drops the
/// baked prelude entry and masks the bug).
/// </summary>
public class RegionPruneCrossEntryTests
{
    private const string Source = @"
main :-
    ( member(2, [1,2,3]) -> G1 = ok ; G1 = bad ), G1 == ok,
    ( append([1,2], [3], [1,2,3]) -> G2 = ok ; G2 = bad ), G2 == ok,
    sum_list([1,2], 3),
    atomic_list_concat([a,b], ab),
    ( once(member(X, [a,b])), X == a -> G3 = ok ; G3 = bad ), G3 == ok.
";

    private static byte[] LinkPruned(bool stripWam)
    {
        var obj = ShmoCompiler.CompileSource(Source, "prunex");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("main", 0) },
            BakePrelude = true,
            IncludeCompiledIl = true,
            StripWam = stripWam,
            RegionPrune = true,
        });
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        return result.Bytes!;
    }

    [Fact]
    public void PrunedStrippedBundle_CrossEntryPreludeCalls_RunViaFromBundle()
    {
        var bundle = BundleReader.FromBytes(LinkPruned(stripWam: true));
        var engine = PrologEngine.FromBundle(bundle);
        Assert.True(engine.Query("main.").Success);
    }

    [Fact]
    public void PrunedBundle_WamKept_CrossEntryPreludeCalls_RunViaFromBundle()
    {
        var bundle = BundleReader.FromBytes(LinkPruned(stripWam: false));
        var engine = PrologEngine.FromBundle(bundle);
        Assert.True(engine.Query("main.").Success);
    }
}
