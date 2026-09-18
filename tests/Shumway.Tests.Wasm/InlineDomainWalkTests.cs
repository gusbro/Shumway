using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-051 phase 3: the module walks a domain's intervals.
///
/// <para>That walk answers three things. $dom_contains outright.
/// $dom_del for the case that removes NOTHING, which is most of them, by
/// handing back the domain it was given. And $dom_singleton, which needs
/// only the arity and the first two bounds.</para>
///
/// <para>A removal that really removes has to build a domain, and that still
/// steps aside. Unbounded domains step aside too: inf and sup are atoms, the
/// comparison would have to be three-way, and no unbounded domain reaches
/// this in the programs measured.</para></summary>
public sealed class InlineDomainWalkTests(ITestOutputHelper o)
{
    private const string Corpus = """
        contains_loop(0, _) :- !.
        contains_loop(N, D) :- '$dom_contains'(D, 5),
                               N1 is N - 1, contains_loop(N1, D).
        run_contains(N) :- '$dom_new'(1, 9, D), contains_loop(N, D).

        absent_loop(0, _) :- !.
        absent_loop(N, D) :- \+ '$dom_contains'(D, 42),
                             N1 is N - 1, absent_loop(N1, D).
        run_absent(N) :- '$dom_new'(1, 9, D), absent_loop(N, D).

        del_loop(0, _) :- !.
        del_loop(N, D) :- '$dom_del'(D, 42, D2), '$dom_same'(D2, D),
                          N1 is N - 1, del_loop(N1, D).
        run_del_noop(N) :- '$dom_new'(1, 9, D), del_loop(N, D).

        single_loop(0, _) :- !.
        single_loop(N, D) :- '$dom_singleton'(D, 7),
                             N1 is N - 1, single_loop(N1, D).
        run_singleton(N) :- '$dom_new'(7, 7, D), single_loop(N, D).

        notsingle_loop(0, _) :- !.
        notsingle_loop(N, D) :- \+ '$dom_singleton'(D, _),
                                N1 is N - 1, notsingle_loop(N1, D).
        run_not_singleton(N) :- '$dom_new'(1, 9, D), notsingle_loop(N, D).

        % These two must reach the host, so they live in predicates that get
        % promoted like the rest: a goal typed straight into a query is not.
        del_present(D2) :- '$dom_new'(1, 9, D), '$dom_del'(D, 5, D2).
        unbounded(V) :- '$dom_new'(1, sup, D), '$dom_contains'(D, V).
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

    /// <summary>Tier-0 and the tier answer the same, case for case. The walk
    /// is emitted by hand and its branch depths are easy to get wrong, which
    /// shows up as a wrong ANSWER rather than a crash.</summary>
    [Theory]
    [InlineData("run_contains(50)")]
    [InlineData("run_absent(50)")]
    [InlineData("run_del_noop(50)")]
    [InlineData("run_singleton(50)")]
    [InlineData("run_not_singleton(50)")]
    // Boundaries: first bound, last bound, and the gaps on either side.
    [InlineData("'$dom_new'(3, 7, D), '$dom_contains'(D, 3)")]
    [InlineData("'$dom_new'(3, 7, D), '$dom_contains'(D, 7)")]
    [InlineData(@"'$dom_new'(3, 7, D), \+ '$dom_contains'(D, 2)")]
    [InlineData(@"'$dom_new'(3, 7, D), \+ '$dom_contains'(D, 8)")]
    // A gap between two intervals, which is what the walk exists for.
    [InlineData(@"'$dom_new'(1, 9, D), '$dom_del'(D, 5, D2), \+ '$dom_contains'(D2, 5)")]
    [InlineData("'$dom_new'(1, 9, D), '$dom_del'(D, 5, D2), '$dom_contains'(D2, 4)")]
    [InlineData("'$dom_new'(1, 9, D), '$dom_del'(D, 5, D2), '$dom_contains'(D2, 6)")]
    // Negative bounds: a payload read unsigned compares wrong against
    // everything, so this is the sign extension.
    [InlineData("'$dom_new'(-9, -1, D), '$dom_contains'(D, -5)")]
    [InlineData(@"'$dom_new'(-9, -1, D), \+ '$dom_contains'(D, 0)")]
    [InlineData(@"'$dom_new'(-9, -1, D), \+ '$dom_contains'(D, -10)")]
    [InlineData("'$dom_new'(-3, 3, D), '$dom_del'(D, 0, D2), '$dom_contains'(D2, -1)")]
    public void TheAnswerIsTheEnginesEitherWay(string goal)
    {
        Assert.True(Plain().Query($"{goal}.").Success, $"Tier-0: {goal}");
        var (tier, _) = Tier();
        Assert.True(tier.Query($"{goal}.").Success, $"tier: {goal}");
    }

    [DiagFact]
    public void ContainsIsAnsweredInTheModule()
    {
        var (tier, exits) = Tier();
        tier.Query("run_contains(50).");
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query("run_contains(500).").Success);
        Assert.True(tier.Query("run_absent(500).").Success);
        o.WriteLine($"$dom_contains exits for 1000 calls: {exits("$dom_contains")}");
        Assert.Equal(0, exits("$dom_contains"));
    }

    /// <summary>A removal that removes nothing never reaches the host, and
    /// the $dom_same after it does not either, because the domain that comes
    /// back is the same cell.</summary>
    [DiagFact]
    public void ARemovalThatRemovesNothingIsAnsweredInTheModule()
    {
        var (tier, exits) = Tier();
        tier.Query("run_del_noop(50).");
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query("run_del_noop(500).").Success);
        o.WriteLine($"$dom_del exits {exits("$dom_del")}, "
                    + $"$dom_same exits {exits("$dom_same")}");
        Assert.Equal(0, exits("$dom_del"));
        Assert.Equal(0, exits("$dom_same"));
    }

    /// <summary>A removal that DOES remove has to build a domain, so it steps
    /// aside. Anti-vacuity for the zero above.</summary>
    [DiagFact]
    public void ARemovalThatRemovesStepsAside()
    {
        var (tier, exits) = Tier();
        tier.Query("del_present(_).");                  // warm: promotes it
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query("del_present(_).").Success);
        Assert.True(exits("$dom_del") >= 1,
            "a removal that removes a value was answered without the host, "
            + "and the module cannot build a domain");
    }

    [DiagFact]
    public void SingletonIsAnsweredInTheModule()
    {
        var (tier, exits) = Tier();
        tier.Query("run_singleton(50).");
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query("run_singleton(500).").Success);
        Assert.True(tier.Query("run_not_singleton(500).").Success);
        o.WriteLine($"$dom_singleton exits for 1000 calls: {exits("$dom_singleton")}");
        Assert.Equal(0, exits("$dom_singleton"));
    }

    /// <summary>Unbounded domains step aside rather than being walked: inf
    /// and sup are atoms and the bound comparison only knows integers.
    /// </summary>
    [DiagFact]
    public void AnUnboundedDomainStepsAside()
    {
        var (tier, exits) = Tier();
        tier.Query("unbounded(5).");                    // warm: promotes it
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query("unbounded(5).").Success);
        Assert.True(exits("$dom_contains") >= 1,
            "an unbounded domain was walked by the module, whose bound "
            + "comparison only knows integers");
    }

    /// <summary>The reserved functor is writable, so the walk checks what it
    /// is walking. These owe a type error.</summary>
    [Theory]
    [InlineData("'$dom_contains'(f(1), 5)")]
    [InlineData("'$dom_contains'(foo, 5)")]
    [InlineData("'$dom_del'(f(1), 5, _)")]
    [InlineData("'$dom_singleton'(f(1), _)")]
    [InlineData("'$dom_singleton'(42, _)")]
    public void AForgedDomainIsNotWalked(string goal)
    {
        var (tier, _) = Tier();
        Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => tier.Query($"{goal}.").Success);
        Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => Plain().Query($"{goal}.").Success);
    }
}
