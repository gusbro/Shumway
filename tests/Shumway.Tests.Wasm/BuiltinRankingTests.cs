using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The builtin exits, attributed. A chain that leaves for a builtin
/// is the tier's dominant cost on the programs where it gains least (queens
/// 12: 626,930 exits against 524,030 chains), and the total alone says a
/// storm happened without saying which one. The tally has accumulated since
/// it was added; this is what reads it.</summary>
public sealed class BuiltinRankingTests(ITestOutputHelper o)
{
    [Fact]
    public void TheRankingNamesTheBuiltinAChainLeavesFor()
    {
        // Arithmetic is open-coded in the module (ADR-018), so a program of
        // is/2 and >/2 never leaves the chain: the exits have to come from a
        // builtin the compiler does not inline.
        var (engine, _) = TieredEngine.Build("""
            lens([], []).
            lens([A|As], [L|Ls]) :- atom_length(A, L), lens(As, Ls).
            names([], []).
            names([A|As], [N|Ns]) :- atom_codes(A, N), names(As, Ns).
            both(Words, Ls, Ns) :- lens(Words, Ls), names(Words, Ns).
            """);
        WasmTierDelegate.ResetDiag();
        Assert.Empty(WasmTierDelegate.BuiltinRanking());

        var r = engine.Query(
            "both([alpha, beta, gamma, delta, epsilon], Ls, _), Ls == [5, 4, 5, 5, 7].");
        Assert.True(r.Success);

        o.WriteLine($"entries={WasmTierDelegate.DiagEntries} "
            + $"builtins={WasmTierDelegate.DiagBuiltins} "
            + $"deopts={WasmTierDelegate.DiagDeopts}");
        var rank = WasmTierDelegate.BuiltinRanking();
        foreach (var (name, arity, hits) in rank)
            o.WriteLine($"{hits,6}  {name}/{arity}");

        Assert.NotEmpty(rank);
        // Ordered by hits, most first: the point of a ranking.
        for (int i = 1; i < rank.Count; i++)
            Assert.True(rank[i - 1].Hits >= rank[i].Hits,
                $"out of order at {i}: {rank[i - 1].Hits} then {rank[i].Hits}");
        // The tally is the same population as the total.
        Assert.Equal(WasmTierDelegate.DiagBuiltins, rank.Sum(x => x.Hits));
        // The two the program leaves for, five words each.
        Assert.Contains(rank, x => x.Name == "atom_length" && x.Arity == 2);
        Assert.Contains(rank, x => x.Name == "atom_codes" && x.Arity == 2);
    }
}
