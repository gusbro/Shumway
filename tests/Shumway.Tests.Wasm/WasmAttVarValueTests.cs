using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>An attributed variable's cell exists ONLY at its home; Deref does
/// not follow it, so a raw copy elsewhere is an orphan the attr table knows
/// nothing about. get_value's two-cell unify copied exactly that when an
/// attvar arrived as the bound side of a bind (found as clpfd corruption:
/// "the given key was not present" out of get_attr under boards.pl). The
/// module now writes Ref(home) instead.</summary>
public class WasmAttVarValueTests
{
    private static PrologEngine TieredEngine(string corpus)
    {
        var engine = new PrologEngine();
        engine.ConsultString(corpus);
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
                members.Add(m);
                try
                {
                    var entry = WasmPredicateCompiler.CompileGroup(members, env);
                    var addrMap = new Dictionary<int, int>(members.Count);
                    foreach (var mm in members) addrMap[mm.Predicate.FunctorId] = mm.Bias;
                    world.InstallGroup(entry.Module, entry.EntryCursorByFid,
                        entry.CursorByAddress, addrMap, entry.RegisterDemand);
                    return new WasmTierDelegate(pred.FunctorId, world).Invoke;
                }
                catch (WasmCompileException)
                {
                    members.Remove(m);
                    return null;
                }
            },
        };
        return engine;
    }

    [Fact]
    public void AnAttvarPassingThroughGetValueKeepsItsIdentity()
    {
        // samep/2's head does the get_value: the fresh second argument is
        // bound to the first — an attvar (dif/2 hangs an attribute on X).
        // The woken machinery must still find the attribute through Y.
        var e = TieredEngine("samep(X, X).");
        WasmTierDelegate.DiagOrphanScan = true;
        try
        {
            Assert.True(e.Query(
                "put_attr(X, m, hello), samep(X, Y), get_attr(Y, m, V), V == hello.")
                .Success);
            // The reverse argument order exercises the other bind branch.
            Assert.True(e.Query(
                "put_attr(X, m, hello), samep(Y, X), get_attr(Y, m, V), V == hello.")
                .Success);
            // And an attvar meeting a plain value through the same path.
            Assert.True(e.Query(
                "put_attr(X, m, hello), samep(X, Y), samep(Y, 7), X == 7.")
                .Success);
        }
        finally { WasmTierDelegate.DiagOrphanScan = false; }
    }
}
