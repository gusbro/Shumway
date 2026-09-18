using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-051 phase 2: domain reads answered inside the module.
///
/// <para>A domain is a term now, so the module can recognise one: the
/// functor's atom, read out of the staged functor table and compared as a
/// relocatable cell, because the arity varies with the interval count and a
/// baked module outlives the process that built it.</para>
///
/// <para>Recognising it is what makes these a speed change rather than a
/// semantic one. A domain is an ordinary term over a reserved functor, so
/// anyone can write one, and a form that answered about `f(1)` would be
/// answering a call that owes a type error. The tests below pin both halves:
/// what the module decides, and what it must not.</para></summary>
public sealed class InlineDomainReadsTests(ITestOutputHelper o)
{
    private const string Corpus = """
        same_loop(0, _) :- !.
        same_loop(N, D) :- '$dom_same'(D, D), N1 is N - 1, same_loop(N1, D).
        run_same(N) :- '$dom_new'(1, 9, D), same_loop(N, D).

        empty_loop(0, _) :- !.
        empty_loop(N, D) :- \+ '$dom_empty'(D), N1 is N - 1, empty_loop(N1, D).
        run_nonempty(N) :- '$dom_new'(1, 9, D), empty_loop(N, D).

        empty_yes_loop(0, _) :- !.
        empty_yes_loop(N, D) :- '$dom_empty'(D), N1 is N - 1, empty_yes_loop(N1, D).
        run_empty(N) :- '$dom_new'(5, 1, D), empty_yes_loop(N, D).

        % Different cells holding different domains: the module has nothing
        % to say and the host answers.
        changed(R) :- '$dom_new'(1, 9, D), '$dom_del'(D, 5, D2),
                      ( '$dom_same'(D2, D) -> R = same ; R = changed ).
        """;

    private static PrologEngine Plain()
    {
        var e = new PrologEngine();
        e.ConsultString(Corpus);
        return e;
    }

    private static (PrologEngine E, System.Func<string, long> Exits) Tier()
    {
        var (tier, _, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        long Exits(string name)
        {
            long n = 0;
            foreach (var (nm, _, hits) in WasmTierDelegate.BuiltinRanking())
                if (nm == name) n += hits;
            return n;
        }
        return (tier, Exits);
    }

    /// <summary>Tier-0 and the tier answer the same, case for case.</summary>
    [Theory]
    [InlineData("run_same(50)")]
    [InlineData("run_nonempty(50)")]
    [InlineData("run_empty(50)")]
    [InlineData("changed(changed)")]
    public void TheAnswerIsTheEnginesEitherWay(string goal)
    {
        Assert.True(Plain().Query($"{goal}.").Success, $"Tier-0: {goal}");
        var (tier, _) = Tier();
        Assert.True(tier.Query($"{goal}.").Success, $"tier: {goal}");
    }

    /// <summary>Identical cells never reach the host.</summary>
    [DiagFact]
    public void SameOnIdenticalCellsIsAnsweredInTheModule()
    {
        var (tier, exits) = Tier();
        tier.Query("run_same(50).");
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query("run_same(500).").Success);
        o.WriteLine($"$dom_same exits for 500 calls: {exits("$dom_same")}");
        Assert.Equal(0, exits("$dom_same"));
    }

    /// <summary>And neither does emptiness, in either direction. The
    /// non-empty answer is the one that matters: clpfd_narrow asks it on the
    /// path where the domain changed, and it is almost always no.</summary>
    [DiagFact]
    public void EmptinessIsAnsweredInTheModule()
    {
        var (tier, exits) = Tier();
        tier.Query("run_nonempty(50).");
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query("run_nonempty(500).").Success);
        o.WriteLine($"$dom_empty exits, non-empty domain: {exits("$dom_empty")}");
        Assert.Equal(0, exits("$dom_empty"));

        var (tier2, exits2) = Tier();
        tier2.Query("run_empty(50).");
        WasmTierDelegate.ResetDiag();
        Assert.True(tier2.Query("run_empty(500).").Success);
        o.WriteLine($"$dom_empty exits, empty domain: {exits2("$dom_empty")}");
        Assert.Equal(0, exits2("$dom_empty"));
    }

    /// <summary>A domain that really changed is a different cell, and the
    /// module steps aside: the answer is the host's. Anti-vacuity for the
    /// zeros above -- the forms are deciding, not disabled.</summary>
    [DiagFact]
    public void ADifferentDomainStepsAside()
    {
        var (tier, exits) = Tier();
        tier.Query("changed(_).");
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query("changed(changed).").Success);
        Assert.True(exits("$dom_same") >= 1,
            "two different domains were decided without the host");
    }

    /// <summary>The reserved functor is writable by anyone, so the forms
    /// check what they are looking at. These owe a type error and must not
    /// be answered in the module.</summary>
    [Theory]
    [InlineData("'$dom_same'(f(1), f(1))")]
    [InlineData("'$dom_same'(foo, foo)")]
    [InlineData("'$dom_same'(42, 42)")]
    [InlineData("'$dom_empty'(f(1))")]
    [InlineData("'$dom_empty'(foo)")]
    public void AForgedDomainIsNotAnsweredInTheModule(string goal)
    {
        var (tier, _) = Tier();
        Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => tier.Query($"{goal}.").Success);
        // And Tier-0 agrees, which is what makes it the right answer.
        Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => Plain().Query($"{goal}.").Success);
    }
}
