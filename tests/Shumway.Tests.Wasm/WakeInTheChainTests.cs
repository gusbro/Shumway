using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-049 point 12: a binding the module made wakes from the chain.
/// The boundary after it asks the host for the interrupt, the driver runs,
/// and the activation resumes in the module -- at the callee, at the
/// continuation, or at the cut or builtin itself -- where it used to step
/// aside and finish the clause on bytecode. Counted, not timed: no deopt,
/// one interrupt per binding, and the answer Tier-0 gives.</summary>
public sealed class WakeInTheChainTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(coroutining)).
        neg_loop(0, _) :- !.
        neg_loop(N, A) :- \+ \+ (A = b), N1 is N - 1, neg_loop(N1, A).
        neg_run(N) :- freeze(A, true), neg_loop(N, A).
        typed(a, W) :- atom(W), !.
        typed(_, no).
        typed_run(N, Ws) :- findall(W, (between(1, N, _), freeze(X, W = yes), typed(X, W)), Ws).
        fact(a).
        fact_run(N) :- findall(X, (between(1, N, _), freeze(X, true), fact(X)), L), length(L, N).
        """;

    private (long Deopts, long Interrupts, long Returns, bool Ok, string Answer) Run(string goal)
    {
        var (engine, members) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query(goal).Success);           // warm: promote
        Assert.NotEmpty(members);
        WasmTierDelegate.ResetDiag();
        var r = engine.Query(goal);
        string answer = r.Success ? string.Join(",", r.Bindings.Select(b => $"{b.Key}={b.Value}")) : "fail";
        o.WriteLine($"{goal}: deopts={WasmTierDelegate.DiagDeopts} interrupts={WasmTierDelegate.DiagWakeInterrupts} "
            + $"returns={WasmTierDelegate.DiagWakeReturns} entries={WasmTierDelegate.DiagEntries} {answer}");
        return (WasmTierDelegate.DiagDeopts, WasmTierDelegate.DiagWakeInterrupts,
                WasmTierDelegate.DiagWakeReturns, r.Success, answer);
    }

    private static string Tier0(string goal)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.ConsultString(Corpus);
        var r = e.Query(goal);
        return r.Success ? string.Join(",", r.Bindings.Select(b => $"{b.Key}={b.Value}")) : "fail";
    }

    [DiagTheory]
    [InlineData("neg_run(50).", "a neck cut after the binding")]
    [InlineData("typed_run(50, Ws).", "a builtin after the binding")]
    [InlineData("fact_run(50).", "a proceed after the binding")]
    public void TheWakeupRunsFromTheChain(string goal, string what)
    {
        var run = Run(goal);
        Assert.True(run.Ok, what);
        Assert.Equal(Tier0(goal), run.Answer);
        Assert.Equal(0L, run.Deopts);
        Assert.True(run.Interrupts >= 50, $"{what}: {run.Interrupts} interrupts for 50 bindings");
        Assert.True(run.Returns >= 50, $"{what}: {run.Returns} driver returns in the chain");
    }
}
