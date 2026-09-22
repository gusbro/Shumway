using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A term walk written the way clp(Z)'s is: a predicate that calls
/// maplist over its arguments with ITSELF as the closure, so the two recur
/// through each other.
///
/// <para>Measured in a browser on clp(Z), the environment chain reached
/// 142,846 frames, alternating <c>lists$maplist/3</c> and
/// <c>clpz$unwrap_with/3</c> -- one frame per NODE of the term instead of
/// one per level. A flat maplist does not do it (20,000 elements peak at a
/// stack of 30), so the mutual recursion is the shape that matters.</para>
///
/// <para>The tree makes the two outcomes impossible to confuse: depth 14 is
/// 16,383 nodes at depth 14, so a chain that tracks DEPTH stays tiny and one
/// that tracks NODES cannot hide.</para></summary>
public sealed class EnvChainGrowthTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(lists)).
        uw(_, V, V) :- var(V), !.
        uw(_, T0, T) :- atomic(T0), !, T = T0.
        uw(G, T0, T) :- T0 =.. [F|A0], maplist(uw(G), A0, A), T =.. [F|A].
        mk(0, leaf) :- !.
        mk(N, f(A, B)) :- N1 is N - 1, mk(N1, A), mk(N1, B).
        run(D, T) :- mk(D, T0), uw(g, T0, T).
        """;

    [DiagFact]
    public void TheChainTracksDepthNotNodes()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query("run(14, T), nonvar(T).").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        WasmTierDelegate.ResetDiag();
        WasmTierDelegate.CpCensusStackAbove = 2000;
        string outcome;
        try { outcome = tiered.Query("run(14, T), nonvar(T).").Success ? "ok" : "failed"; }
        catch (System.Exception ex) { outcome = ex.Message; }
        finally { WasmTierDelegate.CpCensusStackAbove = 1_000_000; }

        o.WriteLine($"tier: {outcome}  maxStack={WasmTierDelegate.DiagMaxStackTop}");
        o.WriteLine($"env: {WasmTierDelegate.DiagEnvCensus ?? "(never sampled)"}");
        o.WriteLine($"cp : {WasmTierDelegate.DiagCpCensus ?? "(never sampled)"}");
        Assert.Equal("ok", outcome);
        // 16,383 nodes at depth 14. A stack that tracks depth stays in
        // the hundreds; one that tracks nodes cannot come near this.
        Assert.True(WasmTierDelegate.DiagMaxStackTop < 5000,
            $"the stack reached {WasmTierDelegate.DiagMaxStackTop} on a walk "
            + "whose depth is 14: it is tracking nodes, not depth");
    }
}
