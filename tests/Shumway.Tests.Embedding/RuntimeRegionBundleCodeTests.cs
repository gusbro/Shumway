using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A region compiled at run time calls a predicate whose code a
/// bundle carries: absorbing it would compile its body a second time, and the
/// calls inside the region would never reach the bundle's code. A predicate
/// still waiting for its code would also stop counting the calls that earn it.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class RuntimeRegionBundleCodeTests : IDisposable
{
    private readonly int _savedThreshold = IlPromotionStore.DefaultPersistedThreshold;

    // Offered, not bound at load: the bundle's code waits for its calls.
    public RuntimeRegionBundleCodeTests() => IlPromotionStore.DefaultPersistedThreshold = 100;

    public void Dispose() => IlPromotionStore.DefaultPersistedThreshold = _savedThreshold;

    private static int Fid(string n, int a) => FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    private static Bundle Library() =>
        BundleReader.FromBytes(ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource("""
                    bk(X, Y) :- X > 0, !, Y is X + 1.
                    bk(X, Y) :- Y is X - 1.
                    """, "bkmod", ShmoBuildMode.Release),
            },
            EntryPoints = new PredicateRef[] { new("bk", 2) },
            StripSource = true,
            IncludeCompiledIl = true,
        }).Bytes!);

    [Fact]
    public void ARegionCompiledAtRunTime_CallsABundlesPredicate()
    {
        var e = PrologEngine.FromBundle(Library());
        e.IlPromotion.Threshold = 1;
        e.IlPromotion.PersistedFreeBytes = 1_000_000;
        e.ConsultString("""
            :- public caller/2.
            caller(X, Y) :- bk(X, Z), Y is Z * 2.
            """);
        int bk = Fid("bk", 2);
        Assert.True(e.IlPromotion.HasOffer(bk), "bk/2 has no offer");
        for (int i = 0; i < 300; i++)
        {
            Assert.Equal("4", $"{e.Query("caller(1, S).")["S"]}");
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        // ANTI-VACUITY: caller/2 runs compiled at run time.
        Assert.True(e.IlPromotion.IsPromoted(Fid("caller", 2)), "caller/2 not promoted");
        Assert.True(e.IlPromotion.IsPromoted(bk), "bk/2 never earned its code: its calls were absorbed");
    }
}
