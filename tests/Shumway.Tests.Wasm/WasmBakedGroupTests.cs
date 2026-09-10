using Shumway.Compiler.Wam;
using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>The ahead-of-time group bake (the web build's embedded prelude):
/// bake from a live engine, serialize, read back, replay-validate, install
/// WITHOUT compiling, and run through it. Tampered evidence must reject.</summary>
public class WasmBakedGroupTests
{
    private const string Corpus = """
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).
        nrev([], []).
        nrev([H|T], R) :- nrev(T, RT), app(RT, [H], R).
        len([], 0).
        len([_|T], N) :- len(T, M), N is M + 1.
        poly(X, R) :- R is X*X + 3*X.
        """;

    private static (PrologEngine Engine, WasmBakedGroup Baked) BakeFrom(string corpus)
    {
        var engine = new PrologEngine();
        engine.ConsultString(corpus);
        engine.IlPromotion.Threshold = 0;
        var store = engine.IlPromotion;
        WasmBakedGroup? baked = null;
        store.Wasm = new WasmPromotionStore(store)
        {
            BatchPromoter = candidates =>
            {
                var members = new List<WasmGroupMember>(candidates.Count);
                foreach (var (pred, addr) in candidates)
                    members.Add(new WasmGroupMember(pred, addr,
                        store.FloatPoolProvider?.Invoke(pred.FunctorId)));
                baked = WasmBakedGroup.BakeCompilable(members, new EngineWasmCompileEnv());
                return baked.Members.Count;
            },
        };
        engine.Query("true.");
        Assert.True(store.Wasm.PromoteAllStatics(engine) > 0);
        Assert.NotNull(baked);
        // Serialize + read back: the installed form is always the read one.
        var ms = new MemoryStream();
        baked!.Write(ms);
        ms.Position = 0;
        return (engine, WasmBakedGroup.Read(ms));
    }

    private static Dictionary<int, CompiledPredicate> ByAddress(PrologEngine engine)
    {
        var d = new Dictionary<int, CompiledPredicate>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
            d[addr] = pred;
        return d;
    }

    [Fact]
    public void BakeValidateInstallAndRunWithoutCompiling()
    {
        var (engine, baked) = BakeFrom(Corpus);
        var store = engine.IlPromotion;
        Assert.True(baked.Validate(new EngineWasmCompileEnv(), ByAddress(engine),
            fid => store.FloatPoolProvider?.Invoke(fid), out string reason), reason);

        var world = new DesktopWasmWorld();
        var entryCursors = new Dictionary<int, int>();
        var entryAddr = new Dictionary<int, int>();
        foreach (var m in baked.Members)
        {
            entryCursors[m.FunctorId] = m.EntryCursor;
            entryAddr[m.FunctorId] = m.Bias;
        }
        var cursors = new Dictionary<int, int>();
        foreach (var kv in baked.CursorByAddress) cursors[kv.Key] = kv.Value;
        world.InstallGroup(baked.Module, entryCursors, cursors, entryAddr,
            baked.RegisterDemand);
        foreach (var m in baked.Members)
            store.RegisterBoundDelegate(m.FunctorId,
                new WasmTierDelegate(m.FunctorId, world).Invoke);

        Assert.True(engine.Query("nrev([1,2,3,4,5], R), R == [5,4,3,2,1].").Success);
        Assert.True(engine.Query("len([a,b,c], 3).").Success);
        Assert.True(engine.Query("poly(5, 40).").Success);
        Assert.False(engine.Query("poly(5, 41).").Success);
        Assert.True(engine.IlPromotion.PromotedFunctorIds().Count()
            >= baked.Members.Count);
    }

    [Fact]
    public void TamperedMarkerRejects()
    {
        var (engine, baked) = BakeFrom(Corpus);
        Assert.True(baked.Markers.Count > 0);
        var markers = new List<(int, int, int)>(baked.Markers);
        markers[0] = (markers[0].Item1, markers[0].Item2, markers[0].Item3 + 7);
        var tampered = new WasmBakedGroup
        {
            Module = baked.Module, RegisterDemand = baked.RegisterDemand,
            Members = baked.Members, CursorByAddress = baked.CursorByAddress,
            Markers = markers, Builtins = baked.Builtins,
            FunctorCount = baked.FunctorCount, AtomCount = baked.AtomCount,
            Functors = baked.Functors,
        };
        var store = engine.IlPromotion;
        Assert.False(tampered.Validate(new EngineWasmCompileEnv(), ByAddress(engine),
            fid => store.FloatPoolProvider?.Invoke(fid), out string reason));
        Assert.Contains("marker", reason);
    }

    [Fact]
    public void ForeignProgramRejects()
    {
        var (_, baked) = BakeFrom(Corpus);
        // A different engine with a different program: the bake's members
        // cannot all match its static link.
        var other = new PrologEngine();
        other.ConsultString("q(1).  q(2).  r(X) :- q(X).");
        other.Query("true.");
        var store = other.IlPromotion;
        Assert.False(baked.Validate(new EngineWasmCompileEnv(), ByAddress(other),
            fid => store.FloatPoolProvider?.Invoke(fid), out _));
    }
}
