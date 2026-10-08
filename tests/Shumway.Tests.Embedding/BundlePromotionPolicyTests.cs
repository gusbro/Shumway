using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-061: what a bundle's compiled code must earn before the
/// compile worker takes it. The first bytes of IL are free past the call
/// threshold, so a small program runs compiled at once; past them a predicate
/// needs calls in proportion to its IL, so a large program does not pay for
/// code it barely runs.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class BundlePromotionPolicyTests : IDisposable
{
    private readonly int _savedThreshold = IlPromotionStore.DefaultPersistedThreshold;

    // The suite binds a bundle's code at load; here it waits.
    public BundlePromotionPolicyTests() => IlPromotionStore.DefaultPersistedThreshold = 32;

    public void Dispose() => IlPromotionStore.DefaultPersistedThreshold = _savedThreshold;

    // climb/2 is called 2000 times a query, once_/2 and top/1 once. Its
    // recursive call is not its last: a compiled predicate's own last call
    // is a jump, which nothing counts.
    private const string Program = """
        climb(0, 0) :- !.
        climb(N, S) :- M is N - 1, climb(M, T), S is T + 1.
        once_(X, Y) :- Y is X + 1.
        top(R) :- climb(1999, _), once_(1, R).
        """;

    private static byte[] Bundle(bool stripWam) =>
        ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Program, "m", ShmoBuildMode.Release) },
            EntryPoints = new PredicateRef[] { new("climb", 2), new("once_", 2), new("top", 1) },
            StripSource = true,
            BakePrelude = true,
            IncludeCompiledIl = true,
            StripWam = stripWam,
        }).Bytes!;

    private static int Fid(string n, int a) => FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    private static void Run(PrologEngine e, int queries)
    {
        for (int i = 0; i < queries; i++) Assert.Equal("2", $"{e.Query("top(R).")["R"]}");
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        // The code that drained in installs at the next dispatch.
        Assert.Equal("2", $"{e.Query("top(R).")["R"]}");
    }

    [Fact]
    public void TheDefaults_AreThirtyTwoCalls_SixtyFourThousandFreeBytes_FourCallsPerByte()
    {
        Assert.Equal((32, 64_000, 4), IlPromotionStore.ReadPersistedDefaults(null));
        Assert.Equal((1000, 0, 0), IlPromotionStore.ReadPersistedDefaults("1000,0,0"));
        Assert.Equal((0, 64_000, 4), IlPromotionStore.ReadPersistedDefaults("0"));
        Assert.Equal((32, 64_000, 7), IlPromotionStore.ReadPersistedDefaults("x,,7"));
        // An engine of a host that sets nothing waits for the threshold: the
        // value this suite's initializer found before it replaced it.
        if (Environment.GetEnvironmentVariable("SHUMWAY_IL_BUNDLE_PROMOTE") is null)
            Assert.Equal(32, BundleCodeAtLoad.ProductDefault);
        var e = new PrologEngine();
        Assert.Equal(32, e.IlPromotion.PersistedThreshold);   // this class's default
        Assert.Equal(64_000, e.IlPromotion.PersistedFreeBytes);
        Assert.Equal(4, e.IlPromotion.PersistedCallsPerByte);
    }

    [Fact]
    public void AtThresholdZero_TheCodeIsBoundAtLoad()
    {
        IlPromotionStore.DefaultPersistedThreshold = 0;
        var e = PrologEngine.FromBundle(BundleReader.FromBytes(Bundle(stripWam: false)));
        Assert.True(e.IlPromotion.IsPromoted(Fid("top", 1)), "top/1 not bound at load");
        Assert.False(e.IlPromotion.HasOffer(Fid("top", 1)), "top/1 offered though bound");
        Assert.Equal("2", $"{e.Query("top(R).")["R"]}");
    }

    [Fact]
    public void WithinTheFreeBytes_APredicateIsTakenAtTheThreshold()
    {
        var e = PrologEngine.FromBundle(BundleReader.FromBytes(Bundle(stripWam: false)));
        e.IlPromotion.PersistedThreshold = 6;
        e.IlPromotion.PersistedFreeBytes = 1_000_000;
        e.IlPromotion.PersistedCallsPerByte = 1_000_000;
        Run(e, 3);
        // Four calls: below the threshold whatever the free bytes.
        Assert.False(e.IlPromotion.IsPromoted(Fid("top", 1)), "top/1 taken below the threshold");
        Assert.True(e.IlPromotion.IsPromoted(Fid("climb", 2)), "climb/2 not taken");
        Run(e, 3);
        Assert.True(e.IlPromotion.IsPromoted(Fid("top", 1)), "top/1 not taken at the threshold");
        Assert.True(e.IlPromotion.IsPromoted(Fid("once_", 2)), "once_/2 not taken at the threshold");
    }

    [Fact]
    public void PastTheFreeBytes_APredicateEarnsItsCodeByItsCalls()
    {
        var e = PrologEngine.FromBundle(BundleReader.FromBytes(Bundle(stripWam: false)));
        e.IlPromotion.PersistedThreshold = 4;
        e.IlPromotion.PersistedFreeBytes = 0;
        e.IlPromotion.PersistedCallsPerByte = 1;
        Run(e, 20);
        // ANTI-VACUITY: 40000 calls of climb/2 pay for the IL of its methods.
        Assert.True(e.IlPromotion.IsPromoted(Fid("climb", 2)), "climb/2 not taken");
        // Twenty-odd calls pay for no predicate's IL at a call a byte.
        Assert.False(e.IlPromotion.IsPromoted(Fid("top", 1)), "top/1 taken before it earned its code");
        Assert.False(e.IlPromotion.IsPromoted(Fid("once_", 2)), "once_/2 taken before it earned its code");
        Assert.True(e.IlPromotion.HasOffer(Fid("top", 1)), "top/1 lost its offer");

        e.IlPromotion.PersistedCallsPerByte = 0;
        Run(e, 1);
        Assert.True(e.IlPromotion.IsPromoted(Fid("top", 1)), "top/1 not taken once it costs nothing");
    }

    private static long Allocated(PrologEngine e, string goal)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(e.Query(goal).Success, goal);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void APredicateThatWaits_IsCountedWithoutAllocating()
    {
        var e = PrologEngine.FromBundle(BundleReader.FromBytes(Bundle(stripWam: false)));
        e.IlPromotion.PersistedThreshold = 4;
        e.IlPromotion.PersistedFreeBytes = 0;
        e.IlPromotion.PersistedCallsPerByte = 1_000_000;
        // The engine's own areas reach their size first.
        for (int i = 0; i < 3; i++)
        {
            Allocated(e, "climb(1999, _).");
            Allocated(e, "climb(199, _).");
        }
        long few = Allocated(e, "climb(199, _).");
        long many = Allocated(e, "climb(1999, _).");
        // ANTI-VACUITY: climb/2 still waits, so each of its calls was counted.
        Assert.True(e.IlPromotion.HasOffer(Fid("climb", 2)), "climb/2 has no offer");
        Assert.False(e.IlPromotion.IsPromoted(Fid("climb", 2)), "climb/2 was taken");
        // 1800 more counts. A closure per count is 40 bytes each.
        Assert.True(many - few < 1800 * 8, $"1800 counts allocated {many - few} bytes");
    }

    [Fact]
    public void AStrippedBundlesContinuationMethods_AreEarnedTheSameWay()
    {
        bool saved = Shumway.Compiler.Il.IlPredicateCompiler.CpsMode;
        byte[] bytes;
        try
        {
            Shumway.Compiler.Il.IlPredicateCompiler.CpsMode = true;
            bytes = Bundle(stripWam: true);
        }
        finally { Shumway.Compiler.Il.IlPredicateCompiler.CpsMode = saved; }
        var e = PrologEngine.FromBundle(BundleReader.FromBytes(bytes));
        e.IlPromotion.PersistedThreshold = 4;
        e.IlPromotion.PersistedFreeBytes = 0;
        e.IlPromotion.PersistedCallsPerByte = 1;
        Run(e, 20);
        // The delegates are bound at load; the continuation methods wait.
        Assert.True(e.IlPromotion.IsPromoted(Fid("top", 1)), "top/1 not bound at load");
        Assert.NotNull(e.IlPromotion.TryGetCps(Fid("climb", 2)));
        Assert.Null(e.IlPromotion.TryGetCps(Fid("top", 1)));
    }
}
