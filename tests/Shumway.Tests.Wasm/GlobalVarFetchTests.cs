using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>'$fetch_global_var'/2 answered from the global-variable image
/// when the key holds a cell live for this activation, the way clp(Z) reads
/// its current propagator on every step (bb_get lowers to it: 5,101 exits
/// on queens, a third of all builtin exits). The image is complete, so a
/// key it lacks is UNSET and the read fails in the module too (clp(Z) reads
/// its current propagator before anything has set it, 6,400 times on
/// queens24); only a snapshot the host has to re-emit goes to the host.
/// </summary>
public sealed class GlobalVarFetchTests(ITestOutputHelper o)
{
    private const string Corpus = """
        loop(0, _) :- !.
        loop(N, S) :- '$fetch_global_var'(cur, C), C == S, N1 is N - 1, loop(N1, S).
        go(N) :- put_attr(S, m, x), b_setval(cur, S), loop(N, S).
        unset :- \+ '$fetch_global_var'(nokey, _).
        undone :- ( b_setval(cur2, a), fail ; \+ '$fetch_global_var'(cur2, _) ).
        loop2(0) :- !.
        loop2(N) :- '$fetch_global_var'(snap, f(X)), X == a, N1 is N - 1, loop2(N1).
        snap(N) :- nb_setval(snap, f(a)), loop2(N).
        loop3(0) :- !.
        loop3(N) :- ( '$fetch_global_var'(nokey, _) -> fail ; true ), N1 is N - 1, loop3(N1).
        others(N) :- b_setval(other, 1), nb_setval(other2, f(b)), loop3(N).
        """;

    [Fact]
    public void TheAnswersAreTheInterpreters()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, _) = TieredEngine.Build(Corpus);
        foreach (string goal in new[] { "go(50).", "unset.", "undone.", "snap(50).", "loop3(50).",
                                        "others(50)." })
        {
            Assert.True(plain.Query(goal).Success, goal);
            Assert.True(tiered.Query(goal).Success, goal + " under the module");
        }
    }

    private static long FetchExits()
    {
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            if (n == "$fetch_global_var" && a == 2) return hits;
        return 0;
    }

    /// <summary>The counters: a live cell is read in the module (no exit,
    /// no step-aside), a snapshot is read through the host (one exit per
    /// read, which is the anti-vacuity: the site still leaves when it has
    /// to).</summary>
    [DiagFact]
    public void ALiveCellIsReadInTheModuleAndASnapshotThroughTheHost()
    {
        var (engine, _) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query("go(3).").Success);
        Assert.True(engine.Query("snap(3).").Success);

        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("go(300).").Success);
        o.WriteLine($"live cell: fetch exits={FetchExits()} deopts={WasmTierDelegate.DiagDeopts}");
        Assert.True(FetchExits() < 300,
            $"{FetchExits()} fetch exits: the module is not reading the image");
        Assert.Equal(0, WasmTierDelegate.DiagDeopts);

        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("snap(300).").Success);
        o.WriteLine($"snapshot: fetch exits={FetchExits()} deopts={WasmTierDelegate.DiagDeopts}");
        Assert.True(FetchExits() >= 300,
            $"{FetchExits()} fetch exits for a snapshot: the corpus stopped reaching the host");
    }

    /// <summary>An unset key fails in the module: the image is complete,
    /// so absence IS the answer -- with other keys live (a cell and a
    /// snapshot) as well as with none at all, the empty image being the
    /// shape that used to send every read out.</summary>
    [DiagFact]
    public void AnUnsetKeyFailsInTheModule()
    {
        var (engine, _) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query("loop3(3).").Success);
        Assert.True(engine.Query("others(3).").Success);
        foreach (string goal in new[] { "loop3(300).", "others(300)." })
        {
            WasmTierDelegate.ResetDiag();
            Assert.True(engine.Query(goal).Success, goal);
            o.WriteLine($"{goal} fetch exits={FetchExits()} deopts={WasmTierDelegate.DiagDeopts}");
            Assert.True(FetchExits() == 0,
                $"{goal}: {FetchExits()} fetch exits for an unset key: the module asked the host");
            Assert.Equal(0, WasmTierDelegate.DiagDeopts);
        }
    }
}
