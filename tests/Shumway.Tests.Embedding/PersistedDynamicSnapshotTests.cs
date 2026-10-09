using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A bundle's compiled snapshot of a dynamic predicate holds the
/// clauses that bundle seeds. When another bundle seeds the same predicate,
/// neither snapshot is the predicate: the engine library bundles both seed
/// attribute_goals/4, and either snapshot alone drops the other library's
/// residual goals.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class PersistedDynamicSnapshotTests : IDisposable
{
    private readonly int _savedThreshold = IlPromotionStore.DefaultPersistedThreshold;

    public PersistedDynamicSnapshotTests() => IlPromotionStore.DefaultPersistedThreshold = 0;

    public void Dispose() => IlPromotionStore.DefaultPersistedThreshold = _savedThreshold;

    private static Bundle Seeding(string module, string value) =>
        BundleReader.FromBytes(ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource($"""
                    :- dynamic shared_hook/1.
                    shared_hook({value}).
                    """, module, ShmoBuildMode.Release),
            },
            EntryPoints = new PredicateRef[] { new("shared_hook", 1) },
            StripSource = true,
            IncludeCompiledIl = true,
        }).Bytes!);

    private static int Fid(string n, int a) => FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    [Fact]
    public void ADynamicPredicateTwoBundlesSeed_RunsTheClausesOfBoth()
    {
        var e = PrologEngine.FromBundle(Seeding("first_seed", "a"));
        // ANTI-VACUITY: the first bundle's snapshot is all the predicate has,
        // and it runs compiled.
        Assert.True(e.IlPromotion.IsPromoted(Fid("shared_hook", 1)), "the first snapshot is not bound");
        Assert.True(e.Query("findall(X, shared_hook(X), [a]).").Success);

        e.LoadBundle(Seeding("second_seed", "b"));
        Assert.True(e.Query("findall(X, shared_hook(X), [a, b]).").Success);
    }

    // The clause asserted first has no bundle snapshot to run as from its
    // first call; the bundle's does, and once it cannot stand for the
    // predicate, the snapshot of every clause takes its place at that call.
    [Fact]
    public void WithTheTierOn_TheSnapshotOfEveryClauseCompilesAtTheFirstCall()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 32;
        Assert.True(e.Query("assertz(shared_hook(z)).").Success);
        e.LoadBundle(Seeding("first_seed", "a"));
        int fid = Fid("shared_hook", 1);
        Assert.False(e.IlPromotion.IsPromoted(fid), "the bundle's snapshot is bound over both clauses");
        Assert.True(e.Query("findall(X, shared_hook(X), [z, a]).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.True(e.IlPromotion.IsPromoted(fid), "not compiled at its first call");
        Assert.True(e.Query("findall(X, shared_hook(X), [z, a]).").Success);
    }

    [Fact]
    public void WithTheTierOff_ADynamicPredicateTwoBundlesSeed_StaysOnBytecode()
    {
        var e = PrologEngine.FromBundle(Seeding("first_seed", "a"));
        e.LoadBundle(Seeding("second_seed", "b"));
        for (int i = 0; i < 40; i++)
            Assert.True(e.Query("findall(X, shared_hook(X), [a, b]).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.False(e.IlPromotion.IsPromoted(Fid("shared_hook", 1)), "compiled with the tier off");
    }
}
