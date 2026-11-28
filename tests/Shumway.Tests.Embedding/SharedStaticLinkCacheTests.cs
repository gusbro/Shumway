using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The cache behind the process-wide static links: full, it drops
/// its oldest entry, not every entry.</summary>
public class OldestOutCacheTests
{
    [Fact]
    public void Full_DropsTheOldestOnly()
    {
        var cache = new OldestOutCache<string, string>(3);
        cache.Add("a", "A");
        cache.Add("b", "B");
        cache.Add("c", "C");
        cache.Add("d", "D");

        Assert.Equal(3, cache.Count);
        Assert.Null(cache.Find("a"));
        Assert.Equal("B", cache.Find("b"));
        Assert.Equal("C", cache.Find("c"));
        Assert.Equal("D", cache.Find("d"));
    }

    [Fact]
    public void AnEntry_OutlivesCapacityMinusOneAdditions()
    {
        var cache = new OldestOutCache<int, string>(4);
        cache.Add(0, "older");
        cache.Add(1, "kept");
        for (int i = 2; i < 5; i++) cache.Add(i, "later");
        Assert.Equal("kept", cache.Find(1));

        cache.Add(5, "later");
        Assert.Null(cache.Find(1));
    }
}

/// <summary>Two engines that run one static program share its link, even
/// when engines running other programs link theirs in between. Tests linking
/// at the same time push the shared link out, so it runs alone, in the
/// gate's exclusive pass.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public class SharedStaticLinkEvictionTests
{
    private const int Capacity = 64;

    [Fact]
    public void ASharedLink_OutlivesTheLinksOfOtherPrograms()
    {
        string run = Guid.NewGuid().ToString("N")[..12];
        // Not empty before the shared program: a cache that empties itself
        // when full then drops the shared link within the loop below.
        RunsAndSharesLink($"sl_filler_{run}");

        string shared = $"sl_shared_{run}";
        Assert.False(RunsAndSharesLink(shared));
        int linksBefore = BundleLoader.StaticLinkBuildCount;
        for (int i = 0; i < Capacity - 1; i++)
            Assert.False(RunsAndSharesLink($"sl_other_{run}_{i}"));

        // Tests linking at the same time (a plain run, not the gate's
        // exclusive pass) add links of their own; past capacity - 1 of them
        // the shared link is rightly the oldest out.
        int linkedSince = BundleLoader.StaticLinkBuildCount - linksBefore;
        if (linkedSince > Capacity - 1) return;
        Assert.True(RunsAndSharesLink(shared),
            "a second engine on the program linked it again after "
            + linkedSince + " other programs were linked");
    }

    // One engine, one new static link: the program is a fresh engine's
    // prelude plus one fact.
    private static bool RunsAndSharesLink(string pred)
    {
        var engine = new PrologEngine();
        engine.ConsultString($":- public {pred}/1.\n{pred}(1).\n");
        Assert.True(engine.Query($"{pred}(X), X == 1.").Success);
        return engine.LastStaticLinkWasSharedHit;
    }
}
