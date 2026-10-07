using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>append(L1, L2, L3) with L1 open enumerates the splits of L3 the
/// way the two-clause definition does: L1 grows by a cell per solution. Built
/// afresh for each split it cost n²/2 cells for the whole enumeration, and the
/// split state held heap cells that a collection between retries left stale.
/// Measured in cells the run allocates, a deterministic count: twice the list
/// must cost about twice the cells.</summary>
public sealed partial class AppendSplitEnumerationTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- public run/1.
        run(L) :- ( append(_, _, L), fail ; true ).
        cells_for(N, Cells) :-
            numlist(1, N, L),
            cells_allocated(C0), run(L), cells_allocated(C1),
            Cells is C1 - C0.
        junk :- numlist(1, 20000, _).
        % Once, at the split before 1001: the garbage below L makes the
        % collection slide L's cells down, and the heap over their old
        % addresses is then overwritten before the next retry.
        split_after_gc(X, Y) :-
            junk, numlist(1, 2000, L),
            append(X, Y, L), Y = [F|_],
            ( F =:= 1001 -> garbage_collect, numlist(1, 30000, _) ; true ),
            F =:= 1501, !.
        """;

    public sealed partial class Probe
    {
        [PrologPredicate("cells_allocated/1")]
        public static long CellsAllocated(Activation engine) => engine.CellsAllocated;
    }

    private static PrologEngine Engine(int threshold)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = threshold;
        e.RegisterPredicates<Probe>();
        e.ConsultString(Corpus);
        return e;
    }

    private long Cells(PrologEngine e, int n)
    {
        var r = e.Query($"cells_for({n}, Cells).");
        Assert.True(r.Success, $"cells_for({n}) failed");
        long cells = long.Parse(r["Cells"]!.ToString()!);
        o.WriteLine($"n {n}: {cells} cells");
        return cells;
    }

    private void Linear(PrologEngine e)
    {
        long small = Cells(e, 2000), large = Cells(e, 4000);
        Assert.True(large < 3 * small,
            $"4000 elements cost {large} cells against {small} for 2000: more than linear");
    }

    [Fact]
    public void Tier0() => Linear(Engine(threshold: 0));

    [Fact]
    public void Il()
    {
        var e = Engine(threshold: 1);
        Assert.True(e.Query("run([a]).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.True(e.IlPromotion.IsPromoted(FunctorTable.Intern(AtomTable.Intern("run").Id, 1)),
            "run/1 did not promote: this would be Tier-0 again");
        Linear(e);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TheSplitSurvivesACollectionBetweenRetries(int threshold)
    {
        var e = Engine(threshold);
        for (int round = 0; round < 3; round++)
        {
            var r = e.Query("split_after_gc(X, Y), Y = [F|_], length(Y, N).");
            Assert.True(r.Success);
            Assert.Equal("1501", r["F"]!.ToString());
            Assert.Equal("500", r["N"]!.ToString());
        }
    }

    [Theory]
    // L1 partial: its given cells must match L3's.
    [InlineData("findall(X-Y, append([a|X], Y, [a, b, c]), Ps), Ps == [[]-[b, c], [b]-[c], [b, c]-[]].")]
    [InlineData("\\+ append([z|_], _, [a, b]).")]
    // L3 improper: every suffix carries its tail.
    [InlineData("findall(X-Y, append(X, Y, [a|foo]), Ps), Ps == [[]-[a|foo], [a]-foo].")]
    // L3 packed.
    [InlineData("findall(X-Y, append(X, Y, \"ab\"), Ps), Ps == [[]-[a, b], [a]-[b], [a, b]-[]].")]
    // L2 partial: it is unified with each suffix in turn.
    [InlineData("findall(X, append(X, [_|_], [a, b]), Xs), Xs == [[], [a]].")]
    // L2 proper pins the split: one answer, sharing L3's suffix.
    [InlineData("append(X, Y, [a, b, c]), Y == [c], !, X == [a, b].")]
    [InlineData("setup_call_cleanup(true, append(X, [c], [a, b, c]), F = done), F == done, X == [a, b].")]
    // The last split leaves no choice point behind.
    [InlineData("setup_call_cleanup(true, append(X, _, [a, b]), F = done), X == [a, b], F == done.")]
    public void TheSplitsAreTheTwoClauseAppendsAnswers(string query)
        => Assert.True(Engine(threshold: 0).Query(query).Success, query);
}
