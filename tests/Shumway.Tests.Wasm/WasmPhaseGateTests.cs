using System.Diagnostics;
using Shumway.Embedding;

namespace Shumway.Tests.Wasm;

/// <summary>The plan's phase 1 gate: the counter, compiled by the REAL
/// compiler (choice point per round, restore, trail unwind -- everything the
/// engine does), still at least twice Tier-0. Measured min-of-five in one
/// process; the assert leaves margin so a noisy runner does not flake it
/// (measured 4.7x on the reference machine; the browser multiplies it by
/// Tier-0's interpretation there).</summary>
public class WasmPhaseGateTests
{
    [Fact]
    public void TheCompiledCounterStaysAheadOfTier0()
    {
        const long N = 2_000_000;
        const string program = "loop(N) :- N > 0, N1 is N - 1, loop(N1).\nloop(0).\n";

        using var h = new WasmProgramHarness(program);
        h.Solve("loop", 200_000);
        double wasm = double.MaxValue;
        for (int r = 0; r < 5; r++)
        {
            var sw = Stopwatch.StartNew();
            Assert.True(h.Solve("loop", N));
            sw.Stop();
            wasm = Math.Min(wasm, sw.Elapsed.TotalMilliseconds * 1e6 / N);
        }

        var engine = new PrologEngine();
        engine.ConsultString(program);
        engine.Query("loop(200000).");
        double tier0 = double.MaxValue;
        for (int r = 0; r < 5; r++)
        {
            var sw = Stopwatch.StartNew();
            Assert.True(engine.Query($"loop({N}).").Success);
            sw.Stop();
            tier0 = Math.Min(tier0, sw.Elapsed.TotalMilliseconds * 1e6 / N);
        }

        Assert.True(tier0 / wasm > 1.5,
            $"wasm {wasm:F1} ns/iter vs Tier-0 {tier0:F1} ns/iter "
            + $"= {tier0 / wasm:F1}x; the gate wants clear air over 2x");
    }
}
