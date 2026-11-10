using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-049's wake driver asks four questions of the engine on every
/// wakeup: the cut barrier, the hook's goal, the proxy to settle, and, when
/// it backtracks over the binding, the extra trail's unwind. Each one was a
/// host crossing; the module answers them itself when the answer is plain
/// (a /4 hook, nothing to settle, entries that only put a cell back) and
/// hands the rest to the host. Counted, not timed, and Tier-0 is the oracle.
/// </summary>
public sealed class WakeSupportInTheModuleTests(ITestOutputHelper o)
{
    private static readonly string[] Support =
        { "$get_cut_barrier/1", "$wake_hook_goal/7", "$wake_settle/2", "$unwind_cp_trails/0" };

    private const string Coroutining = """
        :- use_module(library(coroutining)).
        neg_loop(0, _) :- !.
        neg_loop(N, A) :- \+ \+ (A = b), N1 is N - 1, neg_loop(N1, A).
        neg_run(N) :- freeze(A, true), neg_loop(N, A).
        k(a).
        k(b).
        hookless_run(L) :- findall(X, (put_attr(X, plain, v), k(X)), L).
        mix(X, Y, Z, W) :- ( X = 1, Y = a, Z = 2, W = b, fail ; true ).
        mix_run(R) :-
            freeze(Y, true), freeze(W, true), mix(X, Y, Z, W),
            ( var(X), var(Y), var(Z), var(W) -> R0 = unbound ; R0 = bound ),
            ( frozen(Y, GY), GY \== true -> RY = frozen ; RY = plain ),
            ( frozen(W, GW), GW \== true -> RW = frozen ; RW = plain ),
            R = R0-RY-RW.
        set_run(V) :- b_setval(k, 1), ( b_setval(k, 2), X = 1, fail ; X = 2 ), b_getval(k, V), X == 2.
        """;

    // A Scryer-style module: its verify_attributes/3 hook takes a proxy the
    // host builds and settles.
    private const string Hook3 = """
        :- module(vap, [watch/1, v3_run/2]).
        watch(X) :- put_attr(X, vap, on).
        verify_attributes(Var, Value, [same(Var, Value)]).
        same(A, B) :- A == B.
        pick(a).
        pick(b).
        v3_run(N, L) :- findall(X, (between(1, N, _), watch(X), pick(X)), L).
        """;

    private (string Answer, Dictionary<string, long> Builtins, long Interrupts, long Deopts)
        Run(string corpus, string goal)
    {
        var (engine, members) = TieredEngine.Build(corpus);
        Assert.True(engine.Query(goal).Success, goal);      // warm: promote
        Assert.NotEmpty(members);
        WasmTierDelegate.ResetDiag();
        var r = engine.Query(goal);
        string answer = Answer(r);
        var builtins = new Dictionary<string, long>();
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            builtins[$"{name}/{arity}"] = hits;
        o.WriteLine($"{goal}: {answer} interrupts={WasmTierDelegate.DiagWakeInterrupts} "
            + $"deopts={WasmTierDelegate.DiagDeopts} builtins="
            + string.Join(" ", builtins.Select(b => $"{b.Key}:{b.Value}")));
        return (answer, builtins, WasmTierDelegate.DiagWakeInterrupts, WasmTierDelegate.DiagDeopts);
    }

    private static string Answer(Solution r)
        => r.Success ? string.Join(",", r.Bindings.Select(b => $"{b.Key}={b.Value}")) : "fail";

    private static string Tier0(string corpus, string goal)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.ConsultString(corpus);
        return Answer(e.Query(goal));
    }

    [DiagFact]
    public void AWakeupAtACutAsksTheHostNothing()
    {
        const string goal = "neg_run(50).";
        var run = Run(Coroutining, goal);
        Assert.Equal(Tier0(Coroutining, goal), run.Answer);
        Assert.Equal(0L, run.Deopts);
        Assert.True(run.Interrupts >= 50, $"{run.Interrupts} interrupts for 50 bindings");
        foreach (var name in Support)
            Assert.False(run.Builtins.ContainsKey(name), $"{name} still leaves the module");
    }

    [DiagFact]
    public void AHooklessModuleWakesAndTheModuleSaysSo()
    {
        const string goal = "hookless_run(L).";
        var run = Run(Coroutining, goal);
        Assert.Equal(Tier0(Coroutining, goal), run.Answer);
        Assert.True(run.Interrupts > 0, "the hookless module's binding never woke");
        Assert.False(run.Builtins.ContainsKey("$wake_hook_goal/7"));
    }

    [DiagFact]
    public void BacktrackingOverBindingsOfBothKindsPutsEveryCellBack()
    {
        const string goal = "mix_run(R).";
        var run = Run(Coroutining, goal);
        Assert.Equal(Tier0(Coroutining, goal), run.Answer);
        Assert.Contains("unbound", run.Answer);
        Assert.DoesNotContain("plain", run.Answer);
        Assert.True(run.Interrupts > 0, "the attributed variables were not bound in the module");
        Assert.False(run.Builtins.ContainsKey("$unwind_cp_trails/0"));
    }

    [DiagFact]
    public void AnEntryForManagedStateStillGoesToTheHost()
    {
        const string goal = "set_run(V).";
        var run = Run(Coroutining, goal);
        Assert.Equal(Tier0(Coroutining, goal), run.Answer);
        Assert.Equal("V=1", run.Answer);
        Assert.True(run.Builtins.ContainsKey("$unwind_cp_trails/0"),
            "b_setval's entry was unwound without the host");
    }

    [DiagFact]
    public void AVerify3HookAndItsProxyGoToTheHost()
    {
        const string goal = "v3_run(5, L).";
        var run = Run(Hook3, goal);
        Assert.Equal(Tier0(Hook3, goal), run.Answer);
        Assert.True(run.Interrupts > 0, "the binding never woke from the module");
        Assert.True(run.Builtins.ContainsKey("$wake_hook_goal/7"), "the /3 hook was not the host's");
        Assert.True(run.Builtins.ContainsKey("$wake_settle/2"), "the proxy was not the host's to settle");
    }
}
