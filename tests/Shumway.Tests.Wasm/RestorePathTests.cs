using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Backtracking must stay INSIDE the module. A retry or a trust
/// restores its choice point in wasm; if that path steps aside instead, every
/// backtrack leaves the tier, stages the whole image again and re-enters, and
/// the run pays a boundary crossing per alternative while still answering
/// correctly -- which is why a wrong restore reads as slowness, never as a
/// failure. Only a deopt count catches it.</summary>
public sealed class RestorePathTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- public pick/2.
        :- public pairs/2.
        pick([H|_], H).
        pick([_|T], X) :- pick(T, X).
        pairs(L, X-Y) :- pick(L, X), pick(L, Y), X @< Y.
        """;

    [Fact]
    public void BacktrackingDoesNotLeaveTheTier()
    {
        var (e, members) = TieredEngine.Build(Corpus);
        WasmTierDelegate.ResetDiag();
        // Deep backtracking: every solution drives a retry, the last
        // alternative of each pick drives a trust.
        var r = e.Query("numlist(1, 60, L), findall(P, pairs(L, P), Ps), length(Ps, N).");
        Assert.True(r.Success);
        o.WriteLine($"chains={WasmTierDelegate.DiagEntries} "
            + $"deopts={WasmTierDelegate.DiagDeopts}");
        foreach (var (pc, hits) in WasmTierDelegate.DeoptRanking())
            o.WriteLine($"  0x{pc:X} {hits}");

        // ANTI-VACUITY: the predicates must be on the tier and must actually
        // have run, or "no deopts" is the answer to no question.
        Assert.NotEmpty(members);
        Assert.True(WasmTierDelegate.DiagEntries > 0, "nothing entered the tier");
        // The restore path is pure wasm: nothing here binds an attributed
        // variable, so no step-aside is legitimate.
        Assert.Equal(0, WasmTierDelegate.DiagDeopts);
    }
}
