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
        BuildWithWorld(string corpus)
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var env = new EngineWasmCompileEnv();
        var members = new List<WasmGroupMember>();

        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = 1,
            Promoter = (pred, linkedBase) =>
            {
                var m = new WasmGroupMember(pred, linkedBase,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId));
                try
                {
                    InstallOne(world, m, env);
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
        EngineWasmCompileEnv env)
        => Install(world, new List<WasmGroupMember> { m }, env);

    public static WasmGroupEntry Install(DesktopWasmWorld world,
        List<WasmGroupMember> members, EngineWasmCompileEnv env)
    {
        var entry = WasmPredicateCompiler.CompileGroup(members, env,
            moduleId: world.NextModuleId);
        var addrMap = new Dictionary<int, int>(members.Count);
        foreach (var mm in members) addrMap[mm.Predicate.FunctorId] = mm.Bias;
        entry.InstallInto(world, addrMap);
        return entry;
    }
}
