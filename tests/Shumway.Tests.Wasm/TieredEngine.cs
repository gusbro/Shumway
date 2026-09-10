using Shumway.Compiler.Wasm;
using Shumway.Embedding;

namespace Shumway.Tests.Wasm;

/// <summary>An engine whose predicates promote to a desktop wasm world as
/// they are consulted: the shape the differential tests share.</summary>
internal static class TieredEngine
{
    public static (PrologEngine Engine, List<WasmGroupMember> Members) Build(string corpus)
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
            foreach (var mm in members) addrMap[mm.Predicate.FunctorId] = mm.Bias;
            world.InstallGroup(entry.Module, entry.EntryCursorByFid,
                entry.CursorByAddress, addrMap, entry.RegisterDemand);
        }

        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = 1,
            Promoter = (pred, linkedBase) =>
            {
                var m = new WasmGroupMember(pred, linkedBase,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId));
                members.Add(m);
                try
                {
                    Install();
                    return new WasmTierDelegate(pred.FunctorId, world).Invoke;
                }
                catch (WasmCompileException)
                {
                    members.Remove(m);
                    if (members.Count > 0) Install();
                    return null;
                }
            },
        };
        engine.ConsultString(corpus);
        return (engine, members);
    }
}
