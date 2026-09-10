using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A deopt pays a full image staging, so a run where most chains
/// step aside spends its time on the boundary rather than in wasm. The total
/// says that happened; only a per-site ranking says WHERE, which is what
/// turns "379k deopts" into a list of instructions worth open-coding.</summary>
public sealed class DeoptRankingTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- public walk/2.
        :- public mix/2.
        walk([], []).
        walk([H|T], [X|R]) :- X is H * 2.0, walk(T, R).
        mix(N, S) :- mix_(N, 0, S).
        mix_(0, A, A) :- !.
        mix_(N, A, S) :- A1 is A + N / 2, N1 is N - 1, mix_(N1, A1, S).
        """;

    [Fact]
    public void TheRankingAccountsForEveryDeopt()
    {
        var (e, members) = TieredEngine.Build(Corpus);
        WasmTierDelegate.ResetDiag();
        Assert.True(e.Query("numlist(1, 400, L), walk(L, _), mix(300, _).").Success);

        var rank = WasmTierDelegate.DeoptRanking();
        long sum = 0;
        foreach (var (pc, hits) in rank) { o.WriteLine($"0x{pc:X} {hits}"); sum += hits; }
        o.WriteLine($"total={WasmTierDelegate.DiagDeopts} ranked={sum} "
            + $"overflow={WasmTierDelegate.DiagDeoptOverflow} sites={rank.Count}");

        // ANTI-VACUITY: the corpus must actually be on the tier AND actually
        // deopt, or the accounting below holds trivially.
        Assert.NotEmpty(members);
        Assert.True(WasmTierDelegate.DiagDeopts > 100,
            $"only {WasmTierDelegate.DiagDeopts} deopts: the corpus stopped stepping aside");
        Assert.NotEmpty(rank);
        // Every deopt is attributed: ranked sites plus the overflow tail.
        Assert.Equal(WasmTierDelegate.DiagDeopts,
            sum + WasmTierDelegate.DiagDeoptOverflow);
        // The ranking is ordered heaviest first -- what makes it a work list.
        for (int i = 1; i < rank.Count; i++)
            Assert.True(rank[i - 1].Hits >= rank[i].Hits, "ranking is not sorted");
    }

    /// <summary>The table must be usable WITHOUT a ResetDiag first: a browser
    /// session never calls one, and a table left at its zero default claims
    /// no site at all -- every deopt falls into the overflow and the ranking
    /// reports eight empty rows. That is exactly what shipped.</summary>
    [Fact]
    public void TheTableClaimsSitesWithNoResetFirst()
    {
        Assert.All(WasmTierDelegate.DiagDeoptPcs, pc =>
            Assert.True(pc == -1 || pc > 0,
                "a zero slot reads as occupied by pc 0 and claims nothing"));
    }
}
