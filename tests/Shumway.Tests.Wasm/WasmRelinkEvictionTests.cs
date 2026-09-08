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
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var env = new EngineWasmCompileEnv();
        var members = new List<WasmGroupMember>();
        void Install()
        {
            var entry = WasmPredicateCompiler.CompileGroup(members, env);
            var addrMap = new Dictionary<int, int>(members.Count);
            foreach (var m in members) addrMap[m.Predicate.FunctorId] = m.Bias;
            world.InstallGroup(entry.Module, entry.EntryCursorByFid,
                entry.CursorByAddress, addrMap, entry.RegisterDemand);
        }
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
                members.AddRange(good);
                Install();
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
        wasm.LiveRefreshed = liveByFid =>
        {
            world.RefreshLiveAddresses(liveByFid);
            for (int i = 0; i < members.Count; i++)
                if (liveByFid.TryGetValue(members[i].Predicate.FunctorId, out int at)
                    && members[i].Bias != at)
                    members[i] = members[i] with { Bias = at };
        };
        wasm.StaleEvicted = fids =>
        {
            var gone = new HashSet<int>(fids);
            members.RemoveAll(m => gone.Contains(m.Predicate.FunctorId));
            if (members.Count > 0) Install();
        };
        store.Wasm = wasm;
        return (engine, wasm);
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
}
