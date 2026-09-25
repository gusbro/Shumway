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

        % A bound moves in: the interval narrows, the interval COUNT does
        % not, and the module rebuilds the domain on the heap reusing the
        % functor that is already there. This is the first thing in the arc
        % that WRITES, so the result is checked term for term.
        del_lo(Out)  :- '$dom_new'(1, 9, D), '$dom_del'(D, 1, Out).
        del_hi(Out)  :- '$dom_new'(1, 9, D), '$dom_del'(D, 9, Out).
        del_neg(Out) :- '$dom_new'(-9, -1, D), '$dom_del'(D, -9, Out).
        % Two intervals, and the value is the low bound of the second.
        del_second(Out) :- '$dom_new'(1, 9, D), '$dom_del'(D, 5, D2),
                           '$dom_del'(D2, 6, Out).
        % In a loop, so the rebuild runs often enough to outgrow anything it
        % might be corrupting.
        % The value ADVANCES, or after the first removal it is no longer a
        % bound and the loop stops narrowing anything.
        del_lo_loop(I, N, D, D) :- I > N, !.
        del_lo_loop(I, N, D, Out) :- '$dom_del'(D, I, D2), I1 is I + 1,
                                     del_lo_loop(I1, N, D2, Out).
        run_del_lo(N, Out) :- '$dom_new'(1, 900, D), del_lo_loop(1, N, D, Out).
        unbounded(V) :- '$dom_new'(1, sup, D), '$dom_contains'(D, V).

        % The interval is exactly the value: it disappears, and the count
        % drops. With one interval the whole domain becomes the empty atom.
        del_only(Out) :- '$dom_new'(7, 7, D), '$dom_del'(D, 7, Out).
        % Two intervals, and the second is a single value that goes.
        del_drop_one(Out) :- '$dom_new'(1, 6, D), '$dom_del'(D, 5, D2),
                             '$dom_del'(D2, 6, Out).

        % Fragmented past the functor table's reach. Each removal splits an
        % interval, so after N of them the domain has N + 1 of them, and the
        % module has no functor to build the next one with.
        frag(I, N, D, D) :- I > N, !.
        frag(I, N, D, Out) :- V is I * 2, '$dom_del'(D, V, D2),
                              I1 is I + 1, frag(I1, N, D2, Out).
        shredded(N, Out) :- '$dom_new'(1, 400, D), frag(1, N, D, Out).
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
    // The rebuild: the result is a domain, with the bound moved and nothing
    // else touched.
    [InlineData("del_lo(Out), Out == '$fd_dom'(2, 9)")]
    [InlineData("del_hi(Out), Out == '$fd_dom'(1, 8)")]
    [InlineData("del_neg(Out), Out == '$fd_dom'(-8, -1)")]
    [InlineData("del_second(Out), Out == '$fd_dom'(1, 4, 7, 9)")]
    [InlineData("run_del_lo(100, Out), Out == '$fd_dom'(101, 900)")]
    [InlineData("run_del_lo(1, Out), Out == '$fd_dom'(2, 900)")]
    // Counts that CHANGE: a split (one interval becomes two) and an empty
    // (one interval disappears). Through predicates, so they are promoted.
    [InlineData("del_present(Out), Out == '$fd_dom'(1, 4, 6, 9)")]
    [InlineData("del_only(Out), Out == '$fd_dom_empty'")]
    [InlineData("del_drop_one(Out), Out == '$fd_dom'(1, 4)")]
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

    /// <summary>A removal that narrows an interval is rebuilt in the
    /// module: no host, and the answer is the same term.</summary>
    [DiagFact]
    public void ARemovalAtABoundIsRebuiltInTheModule()
    {
        var (tier, exits) = Tier();
        tier.Query("run_del_lo(10, _).");
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query(
            "run_del_lo(500, Out), Out == '$fd_dom'(501, 900).").Success);
        o.WriteLine($"$dom_del exits for 500 rebuilds: {exits("$dom_del")}");
        Assert.Equal(0, exits("$dom_del"));
    }

    /// <summary>A removal that changes the interval count is rebuilt in
    /// the module too, taking its functor from the table the host stages.
    /// Splitting an interval and emptying one are both this.
    ///
    /// <para>This test asserted the opposite until the table existed, which
    /// is what it was: the module cannot intern a functor, so the host had
    /// to. Now it can look one up.</para></summary>
    [DiagFact]
    public void ARemovalThatChangesTheCountIsRebuiltInTheModule()
    {
        foreach (string p in new[] { "del_present", "del_only", "del_drop_one" })
        {
            var (tier, exits) = Tier();
            tier.Query($"{p}(_).");                     // warm: promotes it
            WasmTierDelegate.ResetDiag();
            Assert.True(tier.Query($"{p}(_).").Success, p);
            o.WriteLine($"{p}: $dom_del exits {exits("$dom_del")}");
            Assert.Equal(0, exits("$dom_del"));
        }
    }

    /// <summary>The functor table is a valve, not a semantic limit: a domain
    /// fragmented past its reach exits to the host, which answers it exactly
    /// as it always did. Anti-vacuity for the zeros above, and the only
    /// remaining way a removal reaches the host.</summary>
    [DiagFact]
    public void ADomainFragmentedPastTheTableStepsAside()
    {
        var (tier, exits) = Tier();
        tier.Query("shredded(4, _).");                  // warm: promotes it
        WasmTierDelegate.ResetDiag();
        // 70 splits: past the table's 64 intervals, so the last few cannot
        // be built in the module.
        Assert.True(tier.Query("shredded(70, _).").Success);
        o.WriteLine($"$dom_del exits for 70 splits: {exits("$dom_del")}");
        Assert.True(exits("$dom_del") >= 1,
            "a domain fragmented past the functor table was still rebuilt in "
            + "the module, which has no functor for it");

        // And the answer is the engine's: compared against Tier-0, not
        // against itself, which is what a wrong rebuild would have passed.
        var plain = Plain();
        var t0 = plain.Query("shredded(70, Out), with_output_to(atom(A), writeq(Out)).");
        var t1 = tier.Query("shredded(70, Out), with_output_to(atom(A), writeq(Out)).");
        Assert.True(t0.Success && t1.Success);
        Assert.Equal(t0.Bindings["A"].ToString(), t1.Bindings["A"].ToString());
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
