using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A cut owes the trails a compaction, and the module cannot pay
/// it.
///
/// <para>The interpreter's Cut lowers B and then COMPACTS, dropping trail
/// entries the cut has made unreachable. Two of the inputs to that decision
/// live in managed side tables the module cannot reach: the catch-frame
/// stack, which raises the survival floor, and the attribute trail log,
/// which is what an AttrModify entry has to be weighed against. So the
/// module checks whether anything was trailed since the barrier -- the
/// barrier's choice point saved the extra-trail top, so it can -- and steps
/// aside when there is, leaving the compaction to the engine.</para>
///
/// <para>What that was worth, on clp(Z)'s SEND+MORE in a browser: the stage
/// went from not finishing in 180 seconds and dying of
/// resource_error(memory) to 868 ms, the live attributed variables at the
/// answer from fourteen to eight -- the same eight, at the same addresses,
/// as the interpreter -- and =../2 from 2,178,087 calls to 81, which is
/// what the interpreter makes.</para></summary>
public sealed class CutCompactsOrStepsAsideTests(ITestOutputHelper o)
{
    private const int CutNeedsCompaction = 28;

    private const string Corpus = """
        :- use_module(library(clpfd)).
        % Narrowing a domain writes an AttrModify entry; the cut then has
        % something on the extra trail to weigh.
        narrow(X) :- X #< 5, !.
        trailed(R) :- X in 1..9, narrow(X), ( X #= 3 -> R = yes ; R = no ).
        % Nothing trailed since the barrier, so the cut has nothing to owe
        % and must NOT step aside: the anti-vacuity half, without which a
        % guard that fired on every cut would pass the test above.
        plain(R) :- p(R).
        p(R) :- q(R), !.
        q(yes).
        """;

    [DiagFact]
    public void ACutWithSomethingTrailedStepsAside()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("trailed(R), R == yes.").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query("trailed(R), R == yes.").Success,
            "the answer moved");
        long g = WasmTierDelegate.DiagMetaGuardHist[CutNeedsCompaction];
        o.WriteLine($"trailed: guard {CutNeedsCompaction} = {g}");
        Assert.True(g > 0,
            "a cut with an attribute change on the extra trail did not step "
            + "aside, so the engine never got to compact");
    }

    [DiagFact]
    public void ACutWithNothingTrailedDoesNot()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("plain(R), R == yes.").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query("plain(R), R == yes.").Success);
        long g = WasmTierDelegate.DiagMetaGuardHist[CutNeedsCompaction];
        o.WriteLine($"plain: guard {CutNeedsCompaction} = {g}");
        Assert.Equal(0L, g);
    }
}
