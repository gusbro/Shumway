using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>jit_compile/1 sets Tier-1 promotion for the goals that follow.
/// A build has exactly one Tier-1 -- the IL compiler here, the WebAssembly
/// backend in WebShumway -- so the same goal carries the same meaning in
/// both, and a harness written in Prolog can ask for a tier setting without
/// knowing which engine it landed in.</summary>
public sealed class JitCompileBuiltinTests
{
    private const string Corpus = """
        count(0, []) :- !.
        count(N, [_|T]) :- count(M, T), N is M + 1.
        len(L, N) :- count(N, L).
        """;

    private static PrologEngine Engine(int threshold = 0)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = threshold;
        e.ConsultString(Corpus);
        return e;
    }

    /// <summary>The list is long enough that "all" and a threshold of 1000
    /// cannot promote the same set, which is what makes the counts below a
    /// test and not a formality.</summary>
    private const string Work =
        "numlist(1, 400, L), len(L, N), N =:= 400.";

    [Fact]
    public void AllPromotesAndOffStopsPromoting()
    {
        var e = Engine();
        Assert.True(e.Query("jit_compile(all).").Success);
        Assert.True(e.Query(Work).Success);
        int promotedUnderAll = e.IlPromotion.PromotedFunctorIds().Count();
        Assert.True(promotedUnderAll > 0,
            "jit_compile(all) promoted nothing: the mode was not established");

        Assert.True(e.Query("jit_compile(off).").Success);
        Assert.True(e.Query(Work).Success);
        Assert.Empty(e.IlPromotion.PromotedFunctorIds());
    }

    /// <summary>off returns what ALREADY promoted, not only what would have.
    /// The eviction is queued and applied at the next query setup, so the
    /// count is taken after a further goal has run.</summary>
    [Fact]
    public void OffReturnsAlreadyPromotedPredicatesToTierZero()
    {
        var e = Engine();
        Assert.True(e.Query("jit_compile(all).").Success);
        Assert.True(e.Query(Work).Success);
        Assert.NotEmpty(e.IlPromotion.PromotedFunctorIds());

        Assert.True(e.Query("jit_compile(off).").Success);
        Assert.True(e.Query("true.").Success);
        Assert.Empty(e.IlPromotion.PromotedFunctorIds());
    }

    /// <summary>Answers do not depend on the mode. This is the property the
    /// conformance corpus checks at scale; here it guards the builtin itself,
    /// since "promote nothing" would pass every count assertion above.
    /// </summary>
    [Theory]
    [InlineData("off")]
    [InlineData("all")]
    [InlineData("2")]
    [InlineData("1000")]
    public void EveryModeAnswersTheSame(string mode)
    {
        var e = Engine();
        Assert.True(e.Query($"jit_compile({mode}).").Success);
        Assert.True(e.Query(Work).Success);
        Assert.True(e.Query("numlist(1, 30, L), len(L, N), N =:= 30.").Success);
    }

    /// <summary>A threshold leaves the rarely-called ones behind, which is
    /// the whole difference between it and "all".
    ///
    /// <para>Counted as each engine's OWN delta across the same goal, not as
    /// totals: an engine starts with whatever its bundle already carries, and
    /// that varies with what else has run in the process. Comparing the
    /// totals passed alone and failed in the full suite.</para></summary>
    [Fact]
    public void AThresholdPromotesLessThanAll()
    {
        static int NewlyPromotedRunning(string mode)
        {
            var e = Engine();
            Assert.True(e.Query($"jit_compile({mode}).").Success);
            var before = new HashSet<int>(e.IlPromotion.PromotedFunctorIds());
            Assert.True(e.Query(Work).Success);
            return e.IlPromotion.PromotedFunctorIds().Count(f => !before.Contains(f));
        }

        int underAll = NewlyPromotedRunning("all");
        int underRare = NewlyPromotedRunning("100000");
        Assert.True(underAll > 0, "jit_compile(all) promoted nothing new");
        Assert.True(underAll > underRare,
            $"all promoted {underAll} and a threshold of 100000 promoted "
            + $"{underRare}: the argument is ignored");
    }

    [Fact]
    public void BadModesAreRejected()
    {
        var e = Engine();
        Assert.Throws<Shumway.Core.PrologRuntimeException>(() => e.Query("jit_compile(sideways).").Success);
        Assert.Throws<Shumway.Core.PrologRuntimeException>(() => e.Query("jit_compile(f(x)).").Success);
        Assert.Throws<Shumway.Core.PrologRuntimeException>(() => e.Query("jit_compile(-3).").Success);
        Assert.Throws<Shumway.Core.PrologRuntimeException>(() => e.Query("jit_compile(X).").Success);
    }

    /// <summary>The mode is engine state, so a directive in a consulted file
    /// sets it for what follows -- the point of the builtin over a top-level
    /// command is that a Prolog harness can ask for the tier itself.
    ///
    /// <para>Asserted on the MODE and not on a promotion count: what promotes
    /// depends on what the engine already carries, and counting it here
    /// passed alone and failed in the full suite. That the mode leads to
    /// promotion is what the tests above are for.</para></summary>
    [Fact]
    public void ADirectiveSetsTheMode()
    {
        var e = new PrologEngine();
        Assert.Equal(0, e.IlPromotion.Threshold);
        e.ConsultString(":- jit_compile(all).\n" + Corpus);
        Assert.Equal(1, e.IlPromotion.Threshold);
        Assert.True(e.Query(Work).Success);

        var off = new PrologEngine();
        off.ConsultString(":- jit_compile(off).\n" + Corpus);
        Assert.Equal(0, off.IlPromotion.Threshold);
        Assert.True(off.Query(Work).Success);
    }
}
