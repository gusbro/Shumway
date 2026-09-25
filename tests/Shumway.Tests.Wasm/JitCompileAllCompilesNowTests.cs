using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Turning the batch on has to compile the program THEN, not at
/// whatever boundary happens to come next.
///
/// <para>The tick's early-out asks whether the static program changed, which
/// is the right question for a consult and the wrong one for a mode change:
/// switching to "all" leaves the program exactly as it was. So the batch did
/// not run, the user's next goal ran interpreted, predicates crossed the
/// threshold while it ran, and the tick AFTER that goal finally compiled
/// everything. Measured in a browser: 107 s for the first
/// countall(queens(9,Qs),N) and 1.8 s for the same goal once the batch had
/// caught up.</para></summary>
public sealed class JitCompileAllCompilesNowTests(ITestOutputHelper o)
{
    private const string Corpus = """
        len([], 0).
        len([_|T], N) :- len(T, M), N is M + 1.
        dbl([], []).
        dbl([X|Xs], [Y|Ys]) :- Y is X * 2, dbl(Xs, Ys).
        work(N, S) :- numlist(1, N, L), dbl(L, D), len(D, S).
        """;

    /// <summary>A world whose batch promoter compiles one module per
    /// predicate, as the browser's lazy grain does.</summary>
    private static (PrologEngine Engine, WasmPromotionStore Wasm, int Builds)
        Tiered(out System.Func<int> builds)
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var env = new EngineWasmCompileEnv();
        int batchCalls = 0;
        var wasm = new WasmPromotionStore(store)
        {
            // Lazy and far away: nothing promotes by hotness in these runs,
            // so anything compiled was compiled BY the batch.
            Threshold = 1_000_000,
            CompileAllOnConsult = false,
            BatchPromoter = candidates =>
            {
                batchCalls++;
                var good = new List<WasmGroupMember>();
                foreach (var (pred, addr) in candidates)
                {
                    var m = new WasmGroupMember(pred, addr,
                        store.FloatPoolProvider?.Invoke(pred.FunctorId));
                    try
                    {
                        WasmPredicateCompiler.CompileGroup(new[] { m }, env);
                        good.Add(m);
                    }
                    catch (WasmCompileException) { }
                }
                if (good.Count == 0) return 0;
                TieredEngine.Install(world, good, env, store);
                foreach (var m in good)
                {
                    store.RegisterBoundDelegate(m.Predicate.FunctorId,
                        new WasmTierDelegate(m.Predicate.FunctorId, world).Invoke);
                    store.Wasm?.NoteInstalled(m.Predicate.FunctorId, m.Bias, m.Predicate);
                }
                return good.Count;
            },
        };
        wasm.LiveRefreshed = world.RefreshLiveAddresses;
        wasm.StaleEvicted = world.Evict;
        store.Wasm = wasm;
        engine.ConsultString(Corpus);
        int Builds() => batchCalls;
        builds = Builds;
        return (engine, wasm, 0);
    }

    /// <summary>Arms the tick's early-out the way a session does: the batch
    /// runs once (in a browser the boot's bundle install is enough on its
    /// own), so the tick has reconciled against this link and recorded it.
    /// Then everything goes back to Tier-0, which is where a user who typed
    /// jit_compile(off) is standing.</summary>
    private static void RunOnceThenTurnEverythingOff(PrologEngine engine,
                                                     WasmPromotionStore wasm)
    {
        Assert.True(engine.Query("work(20, S), S =:= 20.").Success);
        wasm.CompileAllOnConsult = true;
        wasm.Threshold = 1;
        Assert.True(wasm.CompileAllTick(engine) > 0);

        wasm.CompileAllOnConsult = false;
        Assert.True(engine.IlPromotion.SetJitThreshold(0));
        engine.IlPromotion.ApplyPendingJitChange();
        Assert.Empty(engine.IlPromotion.PromotedFunctorIds());
    }

    /// <summary>Turning the batch back on compiles THEN. The program has not
    /// changed since the tick last looked, so only the mode change can ask
    /// for this build.</summary>
    [Fact]
    public void TurningTheBatchOnCompilesWithoutWaitingForTheNextBoundary()
    {
        var (engine, wasm, _) = Tiered(out var builds);
        RunOnceThenTurnEverythingOff(engine, wasm);

        wasm.CompileAllOnConsult = true;
        wasm.Threshold = 1;
        wasm.ForceNextBatch();
        int compiled = wasm.CompileAllTick(engine);
        o.WriteLine($"batch calls={builds()} compiled={compiled} "
            + $"promoted={engine.IlPromotion.PromotedFunctorIds().Count()}");

        Assert.True(compiled > 0,
            "turning the batch on compiled nothing: the tick took its "
            + "program-did-not-change early-out on a MODE change");
        Assert.NotEmpty(engine.IlPromotion.PromotedFunctorIds());
    }

    /// <summary>Counter-proof, and the shape of the bug exactly as it was
    /// reported: off, then all, and the batch does not run. In the browser
    /// the next goal then ran interpreted and the tick after IT compiled
    /// everything.</summary>
    [Fact]
    public void WithoutTheForceTheTickTakesItsEarlyOut()
    {
        var (engine, wasm, _) = Tiered(out _);
        RunOnceThenTurnEverythingOff(engine, wasm);

        wasm.CompileAllOnConsult = true;
        wasm.Threshold = 1;
        Assert.Equal(0, wasm.CompileAllTick(engine));
        Assert.Empty(engine.IlPromotion.PromotedFunctorIds());
    }

    /// <summary>The force is one-shot: a second tick with nothing new to do
    /// still costs a reference compare, or the fix would trade a missed
    /// compile for a build on every query.</summary>
    [Fact]
    public void TheForceIsOneShot()
    {
        var (engine, wasm, _) = Tiered(out _);
        RunOnceThenTurnEverythingOff(engine, wasm);
        wasm.CompileAllOnConsult = true;
        wasm.Threshold = 1;
        wasm.ForceNextBatch();
        Assert.True(wasm.CompileAllTick(engine) > 0);
        Assert.Equal(0, wasm.CompileAllTick(engine));
    }

    /// <summary>The answers are the same on both sides of the switch.</summary>
    [Fact]
    public void TheProgramStillAnswersTheSame()
    {
        var (engine, wasm, _) = Tiered(out _);
        RunOnceThenTurnEverythingOff(engine, wasm);
        Assert.True(engine.Query("work(30, S), S =:= 30.").Success);
        wasm.CompileAllOnConsult = true;
        wasm.Threshold = 1;
        wasm.ForceNextBatch();
        wasm.CompileAllTick(engine);
        Assert.True(engine.Query("work(30, S), S =:= 30.").Success);
        Assert.True(engine.Query("numlist(1, 5, L), dbl(L, D), D == [2,4,6,8,10].").Success);
    }
}
