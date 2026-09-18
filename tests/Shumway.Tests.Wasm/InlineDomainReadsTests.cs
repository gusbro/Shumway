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

        % Different cells holding different domains.
        changed(R) :- '$dom_new'(1, 9, D), '$dom_del'(D, 5, D2),
                      ( '$dom_same'(D2, D) -> R = same ; R = changed ).

        % Where the contents comparison walks. These must be PREDICATES: a
        % goal typed into a query is not promoted, runs on Tier-0, and says
        % nothing about the module. (Written as queries first, they passed
        % with the walk broken.)
        cmp(A, B, R) :- ( '$dom_same'(A, B) -> R = same ; R = differ ).

        first_bound(R)  :- '$dom_new'(1, 9, A), '$dom_new'(2, 9, B), cmp(A, B, R).
        last_bound(R)   :- '$dom_new'(1, 9, A), '$dom_new'(1, 8, B), cmp(A, B, R).
        both_equal(R)   :- '$dom_new'(1, 9, A), '$dom_new'(1, 9, B), cmp(A, B, R).
        mid_bound(R)    :- '$dom_new'(1, 9, A), '$dom_del'(A, 5, A2),
                           '$dom_new'(1, 9, B), '$dom_del'(B, 6, B2),
                           cmp(A2, B2, R).
        two_gaps_same(R):- '$dom_new'(1, 9, A), '$dom_del'(A, 5, A2),
                           '$dom_new'(1, 9, B), '$dom_del'(B, 5, B2),
                           cmp(A2, B2, R).
        arity_differs(R):- '$dom_new'(1, 9, A), '$dom_del'(A, 5, A2), cmp(A2, A, R).
        open_equal(R)   :- '$dom_new'(1, sup, A), '$dom_new'(1, sup, B), cmp(A, B, R).
        open_differs(R) :- '$dom_new'(1, sup, A), '$dom_new'(inf, sup, B),
                           cmp(A, B, R).
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
    // Where the contents comparison walks: a difference in the FIRST bound,
    // in the LAST, and in the middle. The last one is what a walk gets wrong
    // by starting at the functor and stopping one short, which reads as
    // "equal" and is invisible in the answers of the smaller cases.
    [InlineData("first_bound(differ)")]
    [InlineData("last_bound(differ)")]
    [InlineData("both_equal(same)")]
    [InlineData("mid_bound(differ)")]
    [InlineData("two_gaps_same(same)")]
    [InlineData("arity_differs(differ)")]
    // Unbounded bounds are atoms, and equal cells compare equal without the
    // comparison knowing what a bound means.
    [InlineData("open_equal(same)")]
    [InlineData("open_differs(differ)")]
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

    /// <summary>A domain that really changed is a DIFFERENT cell, and phase
    /// 4 compares those bound for bound rather than stepping aside. The
    /// answer is still the engine's answer; what moved is where it is
    /// computed.
    ///
    /// <para>This test used to assert the opposite -- that such a call
    /// reached the host -- which was true of phase 2 and is the limitation
    /// phase 4 lifted. Anti-vacuity now lives where the module still cannot
    /// decide: a forged domain, and a removal that has to build one.</para>
    /// </summary>
    [DiagFact]
    public void ADifferentDomainIsComparedInTheModule()
    {
        var (tier, exits) = Tier();
        tier.Query("changed(_).");
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query("changed(changed).").Success);
        o.WriteLine($"$dom_same exits comparing two different domains: "
                    + $"{exits("$dom_same")}");
        Assert.Equal(0, exits("$dom_same"));
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
