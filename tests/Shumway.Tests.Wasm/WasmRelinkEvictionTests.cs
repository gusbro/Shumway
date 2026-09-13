using Shumway.Compiler.Wam;
using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A wasm module bakes its members' linked ADDRESSES (deopt pcs,
/// resume markers, BP encodings). Loading a library RELINKS the whole static
/// program and moves every address — measured: use_module(library(clpfd))
/// shifts all ~530 prelude predicates — after which a stale delegate's deopt
/// hands the interpreter a pc into what is now different code ("Encountered
/// reserved_invalid opcode … bytecode corruption", the boards.pl crash).
/// The reconciliation tick evicts every delegate whose predicate moved; the
/// batch then recompiles against the live addresses.</summary>
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
                Shumway.Tests.Wasm.TieredEngine.Install(world, good, env);
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
    public void ALibraryLoadMovesEverythingAndTheProgramStaysCorrect()
    {
        var (engine, wasm) = TieredEngine();
        engine.Query("true.");
        int batched = wasm.PromoteAllStatics(engine);
        Assert.True(batched > 100, $"prelude batch promoted only {batched}");

        // The relink: a library load moves the whole static program. The
        // boundary tick refreshes the live-address maps; nothing was
        // redefined, so NOTHING is evicted — the builds stay warm and the
        // batch compiles only what is genuinely new (the library).
        engine.ConsultString(
            ":- use_module(library(clpfd)).\n" +
            "small(X) :- X in 1..3, X #> 1.\n");
        wasm.CompileAllTick(engine);
        Assert.Equal(0, wasm.RelinkEvictions);

        // Prelude predicates — moved, still promoted, translated at the
        // boundary — answer correctly; before the reconciliation this
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
    public void APlainConsultMovesTheCodeAndTheTierTranslates()
    {
        // Even two plain facts relink the whole program: the tick must
        // refresh the maps without evicting anything, or every consult
        // would throw the whole tier away.
        var (engine, wasm) = TieredEngine();
        engine.Query("true.");
        Assert.True(wasm.PromoteAllStatics(engine) > 100);
        engine.ConsultString("plainfact(1).\nplainfact(2).\n");
        wasm.CompileAllTick(engine);
        Assert.Equal(0, wasm.RelinkEvictions);
        Assert.True(engine.Query("plainfact(2).").Success);
        Assert.True(engine.Query("member(b, [a,b]).").Success);
    }

    /// <summary>A redefinition evicts: the functor's rows go to zero, so
    /// its old markers resolve nowhere (a caller left on the tier reaches
    /// it through bytecode), and the next tick compiles the NEW code into a
    /// module of its own. The siblings' modules never change.</summary>
    [Fact]
    public void ARedefinitionEvictsTheFunctorAndTheSiblingsStayOnTheTier()
    {
        var (engine, wasm, world) = TieredEngineWithWorld();
        engine.ConsultString("""
            :- public leaf/1.
            :- public caller/1.
            leaf(1). leaf(2). leaf(3).
            caller(X) :- leaf(X), X > 1.
            """);
        engine.Query("true.");
        Assert.True(wasm.PromoteAllStatics(engine) > 100);
        int leaf = Fid(engine, "leaf", 1), caller = Fid(engine, "caller", 1);
        Assert.True(world.Contains(leaf) && world.Contains(caller), "the corpus is not on the tier");
        int modulesBefore = world.NextModuleId;
        Assert.True(engine.Query("findall(X, caller(X), [2,3]).").Success);

        // A second consult unit ADDS clauses to leaf/1: same functor, new
        // code at a new address (the old version stays as a dead region).
        engine.ConsultString("leaf(5). leaf(6). leaf(7).");
        wasm.CompileAllTick(engine);
        Assert.True(wasm.RelinkEvictions > 0, "the redefinition evicted nothing");
        // caller/1 never changed: same module, still covered.
        Assert.True(world.Contains(caller));
        // The call site in caller/1's module reaches the NEW leaf/1: a
        // sibling call is a table probe, never a baked jump (baked, this
        // answered [2,3] from the dead region while leaf(X) itself answered
        // all six).
        Assert.True(engine.Query("findall(X, caller(X), [2,3,5,6,7]).").Success);
        // leaf/1 came back in a module of its own; nothing was rebuilt.
        Assert.True(world.Contains(leaf));
        Assert.True(world.TryResolve(leaf, 0, out var t) && t.ModuleId >= modulesBefore,
            "the new leaf/1 did not get a fresh module");
        Assert.True(world.TryResolve(caller, 0, out var c) && c.ModuleId < modulesBefore,
            "caller/1 was rebuilt");
    }
}
