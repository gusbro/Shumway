using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>time/1's tallies must survive promotion. A module dispatches its
/// goals and claims its heap cells without touching a managed counter, so a
/// run that stays inside wasm used to report a handful of inferences for
/// millions of goals -- and heap cells are the DETERMINISTIC metric the
/// performance tests compare, which makes a blind counter worse than a
/// cosmetic bug.</summary>
public sealed class TierCountersTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- public pick/2.
        :- public pairs/2.
        :- public build/2.
        :- public chew/2.
        pick([H|_], H).
        pick([_|T], X) :- pick(T, X).
        pairs(L, X-Y) :- pick(L, X), pick(L, Y), X @< Y.
        build(0, []) :- !.
        build(N, [node(N, N, N)|T]) :- N1 is N - 1, build(N1, T).
        chew(0, _) :- !.
        chew(N, L) :- build(300, L0), L0 = [node(A, _, _)|_], A > 0,
                      N1 is N - 1, chew(N1, L).
        """;

    private const string Goal =
        "numlist(1, 40, L), findall(P, pairs(L, P), Ps), length(Ps, _)";

    // Cells claimed BY THE MODULE: structure building in promoted Prolog,
    // with no builtin in the loop to claim them on the managed side. Without
    // this the cell half of the assertion holds even with the tally gone.
    private const string CellGoal = "chew(200, _)";

    private static (long Inferences, long Cells) Measure(PrologEngine e, string goal)
    {
        var w = new System.IO.StringWriter();
        e.Out = w;
        Assert.True(e.Query($"time(({goal})).").Success);
        // "% N inferences, S seconds, M heap cells (L Lips)"
        string s = w.ToString();
        long Field(string after)
        {
            int at = s.IndexOf(after, System.StringComparison.Ordinal);
            Assert.True(at > 0, $"no '{after}' in: {s}");
            int end = at;
            int start = s.LastIndexOf(' ', at - 2) + 1;
            return long.Parse(s[start..(end - 1)].Replace(",", ""),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        return (Field("inferences"), Field("heap cells"));
    }

    [Fact]
    public void PromotionDoesNotBlindTimeSlash1()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (i0, c0) = Measure(plain, Goal);

        var (tiered, members) = TieredEngine.Build(Corpus);
        var (i1, c1) = Measure(tiered, Goal);
        o.WriteLine($"tier0 inf={i0} cells={c0} | wasm inf={i1} cells={c1}");

        // ANTI-VACUITY: nothing is being asserted about the tier unless the
        // corpus is ON it, and unless Tier-0 itself counted a real workload.
        Assert.NotEmpty(members);
        Assert.True(i0 > 1000 && c0 > 1000, $"the oracle counted too little: {i0}/{c0}");

        // The two paths dispatch slightly different goal sequences (what runs
        // promoted and what does not differ), so this bounds the counts rather
        // than equating them. The bug it guards against reported 3 for 4,989.
        Assert.InRange(i1, i0 * 0.8, i0 * 1.2);
        Assert.InRange(c1, c0 * 0.8, c0 * 1.2);
    }

    [Fact]
    public void CellsClaimedInsideTheModuleAreCounted()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (_, c0) = Measure(plain, CellGoal);

        var (tiered, members) = TieredEngine.Build(Corpus);
        var (_, c1) = Measure(tiered, CellGoal);
        o.WriteLine($"tier0 cells={c0} | wasm cells={c1}");

        Assert.NotEmpty(members);
        Assert.True(c0 > 100_000, $"the oracle claimed too little: {c0}");
        Assert.InRange(c1, c0 * 0.8, c0 * 1.2);
    }
}
