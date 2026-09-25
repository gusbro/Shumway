using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>$dom_del hands back the cell it was given when it removes
/// nothing, so an unchanged domain is the SAME term and not a second copy of
/// itself.
///
/// <para>clpfd_narrow runs the pair on every propagation: remove a value,
/// then ask whether the domain changed. Measured in a browser on
/// queens_fd(9), $dom_del and $dom_same together were 82% of every builtin
/// exit in the run.</para>
///
/// <para>The wasm form that answered $dom_same by comparing cells is OFF for
/// now: it recognised a domain by its Foreign tag, and ADR-051 made a domain
/// a term. Phase 2 restores it reading the term, where the comparison is by
/// contents and strictly stronger. What these tests hold on to meanwhile is
/// the half that does not depend on the tier at all -- that the answers are
/// right, and that an unchanged domain costs no heap.</para></summary>
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

        del_loop_present(0, _) :- !.
        del_loop_present(N, D) :- '$dom_del'(D, 5, _),
                                  N1 is N - 1, del_loop_present(N1, D).
        run_del_present(N) :- '$dom_new'(1, 9, D), del_loop_present(N, D).

        % A value that IS in the domain: a different domain, and $dom_same
        % must say no.
        del_present(R) :- '$dom_new'(1, 9, D), '$dom_del'(D, 5, D2),
                          ( '$dom_same'(D2, D) -> R = same ; R = changed ).

        % Equal contents, built apart: equal domains that no comparison of
        % cells can see.
        equal_apart(R) :- '$dom_new'(1, 9, A), '$dom_new'(1, 9, B),
                          ( '$dom_same'(A, B) -> R = same ; R = changed ).
        """;

    private static PrologEngine Plain()
    {
        var e = new PrologEngine();
        e.ConsultString(Corpus);
        return e;
    }

    /// <summary>Tier-0 and the tier answer the same, case for case.</summary>
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

    /// <summary>A removal that removes nothing builds no domain. Measured as
    /// heap cells PER ITERATION, which is byte-identical between runs, so the
    /// comparison is exact: the loop pays one cell an iteration either way
    /// for its own fresh variable, and a removal that really removes pays for
    /// a domain on top of it.</summary>
    [Fact]
    public void AnUnchangedDomainBuildsNothing()
    {
        double absent = CellsPerIteration("run_del_absent");
        double present = CellsPerIteration("run_del_present");
        o.WriteLine($"cells per iteration: absent {absent}, present {present}");

        // The loop's own variable, and nothing else.
        Assert.Equal(1.0, absent);

        // 1..9 minus 5 is two intervals: a functor cell and four bounds, plus
        // the loop's variable. Asserted as a bound rather than a number so a
        // change in how a term is laid out is not a failure here.
        Assert.True(present >= absent + 5,
            $"a removal that removes cost {present} cells an iteration against "
            + $"{absent} for one that does not: either both build a domain or "
            + "neither does");
    }

    /// <summary>Heap cells the loop costs per iteration, from two sizes, so
    /// the query's own fixed setup cancels out.</summary>
    private static double CellsPerIteration(string loop)
    {
        long few = HeapCells($"{loop}(50)");
        long many = HeapCells($"{loop}(500)");
        return (many - few) / 450.0;
    }

    /// <summary>time/1's heap-cell count for a goal: the deterministic
    /// metric (a wall clock is not one).</summary>
    private static long HeapCells(string goal)
    {
        var e = Plain();
        var r = e.Query($"with_output_to(atom(A), time(({goal}))).");
        Assert.True(r.Success, goal);
        string text = r.Bindings["A"].ToString()!;
        int at = text.IndexOf("seconds, ", System.StringComparison.Ordinal);
        Assert.True(at > 0, text);
        string tail = text[(at + 9)..];
        int end = tail.IndexOf(" heap", System.StringComparison.Ordinal);
        return long.Parse(tail[..end].Replace(",", ""),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
