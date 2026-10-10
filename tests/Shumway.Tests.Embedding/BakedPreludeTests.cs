using System.IO;
using System.Text;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An engine installs its prelude compiled, from the bundle the
/// libraries' assembly carries, and the prelude's Tier-1 IL follows the IL
/// tier: offered while the tier is on, never with the engine on Tier-0.</summary>
public class BakedPreludeTests
{
    [Fact]
    public void ANewEngineInstallsThePreludeFromItsBundle()
    {
        Assert.NotNull(LibraryBundles.BakedPrelude);
        var e = new PrologEngine();
        Assert.True(e._precompiledModules.ContainsKey(Prelude.ModuleName));
        var sol = e.Query("sum_list([1, 2, 3], S).");
        Assert.True(sol.Success);
        Assert.Equal("6", sol["S"]!.ToString());
    }

    [Fact]
    public void WithoutTheBundleThePreludeIsConsulted()
    {
        var e = PrologEngine.WithConsultedPrelude();
        Assert.False(e._precompiledModules.ContainsKey(Prelude.ModuleName));
        Assert.NotEmpty(e.Modules[Prelude.ModuleName].Clauses);
        Assert.False(e.IlPromotion.HasOffers);
        Assert.True(e.Query("sum_list([1, 2, 3], 6).").Success);
    }

    [Fact]
    public void ABundleBakedByAnotherBuildIsNotTaken()
    {
        bool read = false;
        Assert.Null(LibraryBundles.AcceptBakedPrelude(
            Encoding.ASCII.GetBytes("another-build"), () => { read = true; return null; }));
        Assert.False(read);
        Assert.Null(LibraryBundles.AcceptBakedPrelude(null, () => { read = true; return null; }));
        Assert.False(read);
        var bytes = BundleWriter.ToBytes(LibraryBundles.BakedPrelude!);
        Assert.NotNull(LibraryBundles.AcceptBakedPrelude(
            Encoding.ASCII.GetBytes(LibraryBundles.EngineBuildStamp!), () => bytes));
    }

    // With the tier on the prelude's code is offered; turned off, the engine
    // runs Tier-0 and the offers go, and they come back with the tier.
    [Fact]
    public void ThePreludesCodeFollowsTheTier()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        Assert.False(e.IlPromotion.HasOffers);
        e.IlPromotion.Threshold = 32;
        Assert.True(e.IlPromotion.HasOffers);
        e.IlPromotion.Threshold = 0;
        Assert.False(e.IlPromotion.HasOffers);
        e.IlPromotion.Threshold = 32;
        Assert.True(e.Query("jit_compile(off).").Success);
        Assert.False(e.IlPromotion.HasOffers);
    }

    // Compiled code counts no inferences: on Tier-0 time/1 reports them,
    // the prelude's compiled code notwithstanding.
    [Fact]
    public void OnTierZeroTimeReportsInferences()
    {
        var sw = new StringWriter();
        var e = new PrologEngine { Out = sw };
        e.IlPromotion.Threshold = 0;
        Assert.True(e.Query("time(sum_list([1, 2, 3], _)).").Success);
        Assert.Contains("inferences,", sw.ToString());
    }
}
