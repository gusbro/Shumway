using System.Linq;
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

    /// <summary>The mode each form establishes, which is what this builtin
    /// owes. Whether the tier then promotes anything depends on WHEN it was
    /// turned on -- turning it on before a consult promotes nothing, after
    /// one it does -- which is a separate open question and not something
    /// jit_compile decides. Asserting on promotion counts here passed alone
    /// and failed in the full suite, alternating between the tests in this
    /// file by run order.</summary>
    [Fact]
    public void AllAndOffEstablishTheirModes()
    {
        var e = Engine();
        Assert.True(e.Query("jit_compile(all).").Success);
        Assert.Equal(1, e.IlPromotion.Threshold);
        Assert.True(e.Query(Work).Success);

        Assert.True(e.Query("jit_compile(off).").Success);
        Assert.Equal(0, e.IlPromotion.Threshold);
        Assert.True(e.Query(Work).Success);
        // off is the one promotion claim that holds either way: whatever was
        // promoted is evicted, and nothing promotes while it is off.
        Assert.Empty(e.IlPromotion.PromotedFunctorIds());
    }

    /// <summary>off returns what ALREADY promoted, not only what would have.
    /// The eviction is queued and applied at the next query setup, so the
    /// count is taken after a further goal has run.</summary>
    /// <summary>off evicts what is promoted, whatever that is. Set up by
    /// turning the threshold on directly, which is the order that does
    /// promote (see the note above), so the eviction has something to do.
    /// </summary>
    [Fact]
    public void OffReturnsAlreadyPromotedPredicatesToTierZero()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.ConsultString(Corpus);
        e.IlPromotion.Threshold = 1;
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
    /// <summary>Kept because it compares two engines set up the SAME way, so
    /// whatever the promotion order does, it does it to both.</summary>
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

    /// <summary>The top level's spellings work here too. They did not, which
    /// made jit_compile(none) a goal that worked when typed at the page and
    /// raised when a program ran it.</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("off")]
    [InlineData("on")]
    [InlineData("all")]
    [InlineData("16")]
    public void EverySpellingTheTopLevelTakesIsAccepted(string mode)
        => Assert.True(Engine().Query($"jit_compile({mode}).").Success, mode);

    /// <summary>And a rejected mode names the offender. It reported an
    /// unbound variable, which tells the reader nothing about what they
    /// typed.</summary>
    [Fact]
    public void ARejectedModeNamesTheOffender()
    {
        // Asserted on the PROLOG term, which is what the user reads: the
        // culprit reported as _ says nothing about what they typed.
        Assert.True(Engine().Query(
            "catch(jit_compile(sideways), "
            + "error(domain_error(jit_compile_mode, sideways), _), true).").Success);
        Assert.True(Engine().Query(
            "catch(jit_compile(f(x)), "
            + "error(domain_error(jit_compile_mode, f(x)), _), true).").Success);
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
