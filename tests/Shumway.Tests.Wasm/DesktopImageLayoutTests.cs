using Shumway.Compiler.Wam;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>The desktop world copies the engine's areas into one linear image,
/// back to back, and hands the module each one's base and limit. The module
/// writes up to the limit, so an area's span is its limit, not what was copied
/// in: the extra trail was staged at its top with the orphan ring right after
/// it while the limit said the whole array, and the first entry a module
/// appended landed in the ring (clp(Z) sudoku came back with an extra-trail
/// entry of type 104).</summary>
public sealed class DesktopImageLayoutTests
{
    private const string Corpus = "app([], L, L).\napp([H|T], L, [H|R]) :- app(T, L, R).\n";

    [Fact]
    public void TheAreasTheModuleWritesDoNotOverlapByTheirLimits()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(Corpus);
        engine.Query("true.");
        var world = new DesktopWasmWorld();
        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (aid, _) = FunctorTable.Lookup(pred.FunctorId);
            if ((AtomTable.GetById(aid)?.Name ?? "").EndsWith("app"))
                members.Add(new WasmGroupMember(pred, addr, null));
        }
        Assert.NotEmpty(members);
        TieredEngine.Install(world, members, new EngineWasmCompileEnv());

        // Some extra-trail entries, so the area has a top below its capacity.
        var act = new Activation();
        for (int i = 0; i < 5; i++)
        {
            int cell = act.AllocateHeap(1);
            act.SetHeap(cell, Cell.Int(i));
            act.TrailValueChange(cell, Cell.Int(-i));
        }

        using IWasmChainContext cx = world.BeginChain(act);
        var areas = new List<(string Name, long Base, long Bytes)>();
        void Add(string name, int baseSlot, int limitSlot, long unit)
        {
            long b = cx.ReadSlot(baseSlot);
            if (b != 0) areas.Add((name, b, cx.ReadSlot(limitSlot) * unit));
        }
        Add("extra trail", WasmAbi.ExtraTrailBase, WasmAbi.ExtraTrailLimit, WasmAbi.ExtraTrailEntryBytes);
        Add("orphan ring", WasmAbi.AttrOrphanBase, WasmAbi.AttrOrphanLimit, 4);
        Add("attr write ring", WasmAbi.AttrWriteBase, WasmAbi.AttrWriteLimit, WasmAbi.AttrWriteEntryInts * 4);
        Add("attr drop ring", WasmAbi.AttrDropBase, WasmAbi.AttrDropLimit, 4);
        Add("attr log", WasmAbi.AttrLogBase, WasmAbi.AttrLogLength, 4);
        Add("arith table", WasmAbi.ArithTableBase, WasmAbi.ArithTableLength, 4);

        // Anti-vacuity: the case that broke is staged -- entries below the top
        // and a ring right after the area.
        Assert.Equal(5, cx.ReadSlot(WasmAbi.ExtraTrailTop));
        Assert.Contains(areas, a => a.Name == "extra trail");
        Assert.Contains(areas, a => a.Name == "orphan ring");
        Assert.True(cx.ReadSlot(WasmAbi.ExtraTrailLimit) > 5,
            "no room above the top: the module could not append an entry at all");

        for (int i = 0; i < areas.Count; i++)
            for (int j = i + 1; j < areas.Count; j++)
            {
                var (n1, b1, s1) = areas[i];
                var (n2, b2, s2) = areas[j];
                Assert.False(b1 < b2 + s2 && b2 < b1 + s1,
                    $"{n1} [{b1}, {b1 + s1}) overlaps {n2} [{b2}, {b2 + s2})");
            }
    }
}
