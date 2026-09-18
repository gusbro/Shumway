using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>$dom_same/2 answered inside the module when the two cells are
/// identical, which is the common case because $dom_del hands back its
/// incoming cell when it removes nothing.
///
/// <para>The pair is what clpfd_narrow does on every propagation: remove a
/// value, then ask whether the domain changed. Measured in a browser on
/// queens_fd(9), $dom_del and $dom_same together were 82% of every builtin
/// exit in the run.</para>
///
/// <para>The form decides only what a comparison can decide. Two DIFFERENT
/// cells holding equal interval lists are equal domains, and seeing that
/// needs the objects, so those step aside to the builtin. The tests below
/// pin both halves: the answers are the engine's either way, and only the
/// exits move.</para></summary>
public sealed class InlineDomSameTests(ITestOutputHelper o)
{
    private const string Corpus = """
        same_loop(0, _) :- !.
        same_loop(N, D) :- '$dom_same'(D, D), N1 is N - 1, same_loop(N1, D).
        run_same(N) :- '$dom_new'(1, 9, D), same_loop(N, D).

        del_loop(0, _) :- !.
        del_loop(N, D) :- '$dom_del'(D, 42, D2), '$dom_same'(D2, D),
                          N1 is N - 1, del_loop(N1, D).
        run_del_absent(N) :- '$dom_new'(1, 9, D), del_loop(N, D).

        % A value that IS in the domain: a different domain, a different
        % cell, and $dom_same must say no.
        del_present(R) :- '$dom_new'(1, 9, D), '$dom_del'(D, 5, D2),
                          ( '$dom_same'(D2, D) -> R = same ; R = changed ).

        % Equal contents, separate objects: equal domains that no comparison
        % of cells can see.
        equal_apart(R) :- '$dom_new'(1, 9, A), '$dom_new'(1, 9, B),
                          ( '$dom_same'(A, B) -> R = same ; R = changed ).
        """;

    private static PrologEngine Plain()
    {
        var e = new PrologEngine();
        e.ConsultString(Corpus);
        return e;
    }

    /// <summary>Tier-0 and the tier answer the same, case for case. The last
    /// two are the ones the form must NOT decide on its own.</summary>
    [Theory]
    [InlineData("run_same(50)")]
    [InlineData("run_del_absent(50)")]
    [InlineData("del_present(changed)")]
    [InlineData("equal_apart(same)")]
    public void TheAnswerIsTheEnginesEitherWay(string goal)
    {
        Assert.True(Plain().Query($"{goal}.").Success, $"Tier-0: {goal}");
        var (tier, _, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        Assert.True(tier.Query($"{goal}.").Success, $"tier: {goal}");
    }

    /// <summary>Identical cells never reach the host.</summary>
    [DiagFact]
    public void IdenticalCellsAreAnsweredInTheModule()
    {
        var (tier, members, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        tier.Query("run_same(50).");
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query("run_same(500).").Success);
        Assert.NotEmpty(members);

        long same = Exits(WasmTierDelegate.BuiltinRanking(), "$dom_same");
        o.WriteLine($"$dom_same exits = {same} for 500 calls");
        Assert.Equal(0, same);
    }

    /// <summary>And the pair: the del that removes nothing keeps the cell,
    /// so the same that follows it is answered too. This is the shape
    /// clpfd_narrow runs, reduced to its two builtins.</summary>
    [DiagFact]
    public void TheDelThatRemovesNothingKeepsItsCell()
    {
        var (tier, _, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        tier.Query("run_del_absent(50).");
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query("run_del_absent(500).").Success);

        var rank = WasmTierDelegate.BuiltinRanking();
        long same = Exits(rank, "$dom_same");
        long del = Exits(rank, "$dom_del");
        o.WriteLine($"$dom_del exits = {del}, $dom_same exits = {same}");
        Assert.Equal(0, same);
        // Anti-vacuity: the del still exits, so the loop really ran 500
        // times and the zero above is an answered call and not an absent one.
        Assert.True(del >= 500, $"only {del} $dom_del exits: the loop did not run");
    }

    /// <summary>A domain that really changed steps aside, or the form would
    /// be answering with a comparison what needs the objects.</summary>
    [DiagFact]
    public void ADifferentDomainStepsAside()
    {
        var (tier, _, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        tier.Query("equal_apart(_).");
        WasmTierDelegate.ResetDiag();
        Assert.True(tier.Query("equal_apart(same).").Success);
        Assert.True(Exits(WasmTierDelegate.BuiltinRanking(), "$dom_same") >= 1,
            "equal domains in separate objects were decided without the host");
    }

    private static long Exits(List<(string Name, int Arity, long Hits)> rank, string name)
    {
        long n = 0;
        foreach (var (nm, _, hits) in rank) if (nm == name) n += hits;
        return n;
    }
}
