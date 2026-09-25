using Shumway.Compiler.Wasm;
using Shumway.Embedding;

namespace Shumway.Tests.Wasm;

/// <summary>An engine whose predicates promote to a desktop wasm world as
/// they are consulted, ONE MODULE PER PREDICATE: the shape the differential
/// tests share, and the grain the browser's lazy mode uses. Every call or
/// backtrack between two predicates crosses a module boundary, so a corpus
/// built here exercises the in-wasm hop everywhere.</summary>
internal static class TieredEngine
{
    public static (PrologEngine Engine, List<WasmGroupMember> Members) Build(string corpus)
    {
        var (engine, members, _) = BuildWithWorld(corpus);
        return (engine, members);
    }

    public static (PrologEngine Engine, List<WasmGroupMember> Members, DesktopWasmWorld World)
        BuildWithWorld(string corpus) => BuildWithWorld(corpus, wasmThreshold: 1);

    /// <summary>Same, with the wasm promotion threshold exposed: 1 promotes a
    /// predicate on its first call ("all" in the conformance runner), a
    /// higher value leaves the rarely-called ones on Tier-0 and promotes the
    /// hot ones mid-run ("jit"), which exercises the Tier-0-to-wasm handover
    /// in flight rather than before the query.</summary>
    public static (PrologEngine Engine, List<WasmGroupMember> Members, DesktopWasmWorld World)
        BuildWithWorld(string corpus, int wasmThreshold)
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var env = new EngineWasmCompileEnv();
        var members = new List<WasmGroupMember>();

        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = wasmThreshold,
            Promoter = (pred, linkedBase) =>
            {
                var m = new WasmGroupMember(pred, linkedBase,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId));
                try
                {
                    InstallOne(world, m, env, store);
                    members.Add(m);
                    return new WasmTierDelegate(pred.FunctorId, world).Invoke;
                }
                catch (WasmCompileException)
                {
                    return null;
                }
            },
        };
        engine.ConsultString(corpus);
        return (engine, members, world);
    }

    /// <summary>Compiles the member as a module of its own against the id
    /// the world will give it, and installs it.</summary>
    public static void InstallOne(DesktopWasmWorld world, WasmGroupMember m,
        EngineWasmCompileEnv env, IlPromotionStore? store = null)
        => Install(world, new List<WasmGroupMember> { m }, env, store);

    public static WasmGroupEntry Install(DesktopWasmWorld world,
        List<WasmGroupMember> members, EngineWasmCompileEnv env,
        IlPromotionStore? store = null)
        => Install(world, members, env, store, out _);

    /// <summary>Also reports the functors of other modules the install
    /// displaced (the baked callers of a taken-over member).</summary>
    public static WasmGroupEntry Install(DesktopWasmWorld world,
        List<WasmGroupMember> members, EngineWasmCompileEnv env,
        IlPromotionStore? store, out IReadOnlyList<int> displaced)
    {
        var entry = WasmPredicateCompiler.CompileGroup(members, env,
            moduleId: world.NextModuleId);
        var addrMap = new Dictionary<int, int>(members.Count);
        foreach (var mm in members) addrMap[mm.Predicate.FunctorId] = mm.Bias;
        displaced = entry.InstallInto(world, addrMap);
        // The store keeps a delegate per functor; a displaced one would keep
        // bailing out of the tier on every call instead of re-promoting.
        if (displaced.Count > 0 && store is not null) store.Wasm?.Displaced(displaced);
        return entry;
    }
}
