using Shumway.Compiler.Wam;
using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A wasm module bakes its members' linked ADDRESSES (deopt pcs,
/// resume markers, BP encodings), and a stale one hands the interpreter a pc
/// into what is now different code ("Encountered reserved_invalid opcode …
/// bytecode corruption", the boards.pl crash).
///
/// <para>The static layout is append-only, so a consult and a library load
/// move nothing (measured, both). What moves an address is the space below
/// it closing up: a predicate that DISAPPEARS leaves a hole the next relink
/// fills. The tier absorbs that by translating at the boundary rather than
/// by requiring frozen addresses, which is what keeps a more aggressive
/// compaction of the code space open to us. A REDEFINED predicate is a
/// different case: its code changed, so the tick evicts it (and the baked
/// callers that jump into it) instead of translating.</para></summary>
public class WasmRelinkEvictionTests
{
    private static (PrologEngine Engine, WasmPromotionStore Wasm) TieredEngine()
    {
        var (e, w, _) = TieredEngineWithWorld();
        return (e, w);
    }

    /// <summary>The batch shape: each consult's new candidates become ONE
    /// module; an evicted functor leaves the rows and is recompiled, into a
    /// fresh module, at the next tick.</summary>
    private static (PrologEngine Engine, WasmPromotionStore Wasm, DesktopWasmWorld World)
        TieredEngineWithWorld()
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var env = new EngineWasmCompileEnv();
        var wasm = new WasmPromotionStore(store)
        {
            Threshold = 1,
            CompileAllOnConsult = true,
            BatchPromoter = candidates =>
            {
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
                Shumway.Tests.Wasm.TieredEngine.Install(world, good, env, store);
                foreach (var m in good)
                {
                    store.RegisterBoundDelegate(m.Predicate.FunctorId,
                        new WasmTierDelegate(m.Predicate.FunctorId, world).Invoke);
                    store.Wasm?.NoteInstalled(m.Predicate.FunctorId, m.Bias,
                        m.Predicate);
                }
                return good.Count;
            },
        };
        wasm.LiveRefreshed = world.RefreshLiveAddresses;
        wasm.StaleEvicted = world.Evict;
        store.Wasm = wasm;
        return (engine, wasm, world);
    }

    /// <summary>The functor of a static predicate, by (possibly qualified) name.</summary>
    private static int Fid(PrologEngine engine, string name, int arity)
    {
        foreach (var (_, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (aid, ar) = Shumway.Core.FunctorTable.Lookup(pred.FunctorId);
            if (ar == arity && (Shumway.Core.AtomTable.GetById(aid)?.Name ?? "").EndsWith(name))
                return pred.FunctorId;
        }
        throw new Xunit.Sdk.XunitException($"{name}/{arity} is not a static predicate");
    }

    [Fact]
    public void ALibraryLoadEvictsNothingAndTheProgramStaysCorrect()
    {
        var (engine, wasm) = TieredEngine();
        engine.Query("true.");
        int batched = wasm.PromoteAllStatics(engine);
        Assert.True(batched > 100, $"prelude batch promoted only {batched}");

        // The relink a library load forces: nothing was redefined, so
        // NOTHING is evicted — the builds stay warm and the batch compiles
        // only what is genuinely new (the library).
        engine.ConsultString(
            ":- use_module(library(clpfd)).\n" +
            "small(X) :- X in 1..3, X #> 1.\n");
        wasm.CompileAllTick(engine);
        Assert.Equal(0, wasm.RelinkEvictions);

        // Prelude predicates — still promoted, still at their addresses —
        // answer correctly; before the reconciliation this
        // crashed with "reserved_invalid opcode … bytecode corruption".
        // (The clpfd LIBRARY's own predicates under the tier are a separate
        // front: promoted attvar-heavy code has its own open issue, so this
        // test drives the relink with the load and verifies the survivors.)
        Assert.True(engine.Query("member(b, [a,b,c]).").Success);
        Assert.False(engine.Query("member(z, [a,b,c]).").Success);
        Assert.True(engine.Query("msort([3,1,2], [1,2,3]).").Success);
        Assert.True(engine.Query("numlist(1, 5, [1,2,3,4,5]).").Success);
    }

    [Fact]
    public void APlainConsultKeepsEveryAddressAndEvictsNothing()
    {
        // Two plain facts relink the whole program, and the append-only
        // layout means nothing moves: the tick must evict nothing, or every
        // consult would throw the whole tier away.
        var (engine, wasm) = TieredEngine();
        engine.Query("true.");
        Assert.True(wasm.PromoteAllStatics(engine) > 100);
        engine.ConsultString("plainfact(1).\nplainfact(2).\n");
        wasm.CompileAllTick(engine);
        Assert.Equal(0, wasm.RelinkEvictions);
        Assert.True(engine.Query("plainfact(2).").Success);
        Assert.True(engine.Query("member(b, [a,b]).").Success);
    }

    /// <summary>The case the boundary translation exists for. Loading a
    /// library runs its directives, which leave anonymous helpers ($disj_N,
    /// $neg_N) behind; the next consult drops them, the hole closes, and
    /// every predicate above it slides down by the same amount (measured:
    /// 286 of clpfd's own by -785). Nothing was redefined, so nothing is
    /// evicted: the builds keep running, translated at the boundary. Delete
    /// the translation and this is the "bytecode corruption" crash again --
    /// and the code space could never compact.</summary>
    [DiagFact]
    public void AConsultAfterALibraryLoadCompactsAndTheTierTranslates()
    {
        var (engine, wasm, world) = TieredEngineWithWorld();
        int moves = 0, movedAtLeast = 0;
        wasm.LiveRefreshed = live =>
        {
            moves++;
            int n = 0;
            foreach (var (fid, addr) in live)
                if (_before.TryGetValue(fid, out int was) && was != addr) n++;
            movedAtLeast = System.Math.Max(movedAtLeast, n);
            world.RefreshLiveAddresses(live);
        };
        engine.Query("true.");
        Assert.True(wasm.PromoteAllStatics(engine) > 100);

        engine.ConsultString(":- use_module(library(clpfd)).");
        wasm.CompileAllTick(engine);
        Assert.Equal(0, wasm.RelinkEvictions);
        Snapshot(engine);

        // The consult that closes the hole the directives left.
        engine.ConsultString("after(1). after(2).");
        wasm.CompileAllTick(engine);

        Assert.True(moves > 0,
            "no address moved: the consult after a library load used to "
            + "compact the space its directive helpers left behind");
        Assert.True(movedAtLeast > 50,
            $"only {movedAtLeast} predicates moved, expected the library's block");

        // The predicates that MOVED are the library's own, so the queries
        // have to run those: asking the prelude (which did not move) proves
        // nothing about the translation.
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("X in 1..3, X #> 1, X #< 3, label([X]), X == 2.").Success);
        Assert.True(engine.Query(
            "Vs = [A,B], Vs ins 1..2, all_distinct(Vs), A #< B, label(Vs), "
            + "A == 1, B == 2.").Success);
        Assert.False(engine.Query("Y in 1..2, Y #> 5, label([Y]).").Success);
        Assert.True(engine.Query("member(b, [a,b,c]).").Success);
        Assert.True(engine.Query("after(2).").Success);
        Assert.True(WasmTierDelegate.DiagEntries > 0,
            "nothing ran as wasm after the move: the tier fell back");
    }

    private Dictionary<int, int> _before = new();

    /// <summary>The layout as it stands, so a later refresh can say what
    /// this step moved.</summary>
    private void Snapshot(PrologEngine e)
    {
        _before = new Dictionary<int, int>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(e))
            _before[pred.FunctorId] = addr;
    }

    /// <summary>A redefinition evicts: the functor's rows go to zero, and
    /// the members of its module that reach it by a baked jump go with it
    /// (their jumps would land in the dead region: baked and NOT dragged,
    /// caller/1 answered [2,3] while leaf(X) itself answered all six). The
    /// next tick compiles the new code and the dragged callers into a fresh
    /// module; a sibling that never calls the redefined functor stays where
    /// it was. Nothing else is rebuilt.</summary>
    [Fact]
    public void ARedefinitionEvictsTheFunctorAndItsBakedCallers()
    {
        var (engine, wasm, world) = TieredEngineWithWorld();
        engine.ConsultString("""
            :- public leaf/1.
            :- public caller/1.
            :- public bystander/1.
            leaf(1). leaf(2). leaf(3).
            caller(X) :- leaf(X), X > 1.
            bystander(X) :- X = 1.
            """);
        engine.Query("true.");
        Assert.True(wasm.PromoteAllStatics(engine) > 100);
        int leaf = Fid(engine, "leaf", 1), caller = Fid(engine, "caller", 1),
            bystander = Fid(engine, "bystander", 1);
        Assert.True(world.Contains(leaf) && world.Contains(caller) && world.Contains(bystander),
            "the corpus is not on the tier");
        int modulesBefore = world.NextModuleId;
        Assert.True(engine.Query("findall(X, caller(X), [2,3]).").Success);

        // A second consult unit ADDS clauses to leaf/1: same functor, new
        // code at a new address (the old version stays as a dead region).
        engine.ConsultString("leaf(5). leaf(6). leaf(7).");
        wasm.CompileAllTick(engine);
        // leaf/1 and the dragged caller/1, at least.
        Assert.True(wasm.RelinkEvictions >= 2, $"evicted {wasm.RelinkEvictions}, expected the caller too");
        Assert.True(engine.Query("findall(X, caller(X), [2,3,5,6,7]).").Success);
        // Both came back in a fresh module; the bystander never moved.
        Assert.True(world.TryResolve(leaf, 0, out var t) && t.ModuleId >= modulesBefore,
            "the new leaf/1 did not get a fresh module");
        Assert.True(world.TryResolve(caller, 0, out var c) && c.ModuleId >= modulesBefore,
            "caller/1 was not re-promoted against the new leaf/1");
        Assert.True(world.TryResolve(bystander, 0, out var b) && b.ModuleId < modulesBefore,
            "bystander/1 was rebuilt");
    }
}
