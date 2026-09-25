using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The attribute table's image has to say exactly what the store
/// says, read the way a MODULE reads it: an i64 load at a computed address in
/// linear memory, not a managed call.
///
/// <para>The probe below is written out rather than calling the engine's own
/// lookup, and that is the point. It is an independent re-derivation of the
/// layout -- the same one the emitter will hard-code -- so it fails if the
/// staged offset, the key packing or the mask convention is wrong. Calling
/// Activation's lookup would only prove the engine agrees with itself.</para>
///
/// <para>Until the emitter lands nothing in generated code reads this image,
/// so a staging bug would otherwise sit silent and surface later as a wrong
/// answer out of compiled code, far from its cause.</para></summary>
public sealed class AttrTableAgreesTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- public app/3.
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).
        """;

    /// <summary>The module's probe: hash, then walk on equal keys, stopping at
    /// an empty slot and stepping over tombstones.</summary>
    private static int LookupInImage(IWasmChainContext cx,
        long tableBase, int mask, int home, int moduleId)
    {
        uint h = (uint)home * 2654435761u + (uint)moduleId * 2246822519u;
        h ^= h >> 15;
        int slot = (int)(h & (uint)mask);
        long key = ((long)(home + 1) << 32) | (uint)moduleId;
        for (int guard = 0; guard <= mask; guard++)
        {
            long k = cx.ReadWord(tableBase + slot * 16L);
            if (k == 0) return -1;
            if (k == key) return (int)cx.ReadWord(tableBase + slot * 16L + 8);
            slot = (slot + 1) & mask;
        }
        return -1;
    }

    [Fact]
    public void EveryAttributeIsReadableFromLinearMemory()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(Corpus);
        engine.Query("true.");

        var env = new EngineWasmCompileEnv();
        var world = new DesktopWasmWorld();
        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (aid, _) = FunctorTable.Lookup(pred.FunctorId);
            if ((AtomTable.GetById(aid)?.Name ?? "").EndsWith("app"))
                members.Add(new WasmGroupMember(pred, addr, null));
        }
        Assert.NotEmpty(members);
        TieredEngine.Install(world, members, env);

        // A fresh activation, not the consulting engine's: the staging under
        // test copies the areas and the image out of whatever activation opens
        // the chain, and nothing here ever CALLS the module.
        var act = new Activation();
        var expected = new List<(int Home, int Module, int Value)>();
        for (int i = 0; i < 40; i++)
        {
            int home = act.AllocateHeapUnbound();
            for (int m = 1; m <= 3; m++)
            {
                int v = act.AllocateHeap(1);
                act.SetHeap(v, Cell.Atom(500 + i * 4 + m));
                act.PutAttr(home, m, v);
                expected.Add((home, m, v));
            }
        }
        // One removed again, so the image has to carry a HOLE and not just a
        // prefix: a probe that stopped at the tombstone would hide the rest.
        act.DelAttr(expected[5].Home, expected[5].Module);
        var removed = expected[5];
        expected.RemoveAt(5);

        using IWasmChainContext cx = world.BeginChain(act);
        long tableBase = cx.ReadSlot(WasmAbi.AttrTableBase);
        int mask = (int)cx.ReadSlot(WasmAbi.AttrTableMask);
        Assert.True(tableBase != 0, "the chain staged no attribute image");
        Assert.True(mask > 0, $"mask {mask} is not a power-of-two table");

        foreach (var (home, module, value) in expected)
            Assert.Equal(value, LookupInImage(cx, tableBase, mask, home, module));

        Assert.Equal(-1,
            LookupInImage(cx, tableBase, mask, removed.Home, removed.Module));
        // A home that was never attributed: an image that answered anything
        // here would make get_attr/3 succeed on a plain variable.
        Assert.Equal(-1,
            LookupInImage(cx, tableBase, mask, expected[0].Home, 99));

        o.WriteLine($"{expected.Count} attributes read from linear memory,"
                    + $" table at 0x{tableBase:X} mask 0x{mask:X}");
        // ANTI-VACUITY: an empty expectation would pass every loop above.
        Assert.True(expected.Count > 100, $"only {expected.Count} compared");
    }

    /// <summary>A mutation between two calls of the SAME chain has to reach
    /// the image. The host runs a builtin with the chain open -- put_attr/3
    /// itself is one -- and re-enters; the module would otherwise keep reading
    /// what was true before the builtin ran.</summary>
    [Fact]
    public void AMutationMidChainIsVisibleAfterTheRestaging()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(Corpus);
        engine.Query("true.");

        var env = new EngineWasmCompileEnv();
        var world = new DesktopWasmWorld();
        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (aid, _) = FunctorTable.Lookup(pred.FunctorId);
            if ((AtomTable.GetById(aid)?.Name ?? "").EndsWith("app"))
                members.Add(new WasmGroupMember(pred, addr, null));
        }
        TieredEngine.Install(world, members, env);

        // A fresh activation, not the consulting engine's: the staging under
        // test copies the areas and the image out of whatever activation opens
        // the chain, and nothing here ever CALLS the module.
        var act = new Activation();
        int home = act.AllocateHeapUnbound();
        int v0 = act.AllocateHeap(1);
        act.SetHeap(v0, Cell.Atom(700));
        act.PutAttr(home, 1, v0);

        using IWasmChainContext cx = world.BeginChain(act);
        long b0 = cx.ReadSlot(WasmAbi.AttrTableBase);
        int m0 = (int)cx.ReadSlot(WasmAbi.AttrTableMask);
        Assert.Equal(v0, LookupInImage(cx, b0, m0, home, 1));

        // Exactly what the builtin path does around managed work.
        cx.SyncEngine();
        int v1 = act.AllocateHeap(1);
        act.SetHeap(v1, Cell.Atom(701));
        act.PutAttr(home, 1, v1);
        act.PutAttr(home, 2, v1);
        cx.RefreshFromEngine();

        long b1 = cx.ReadSlot(WasmAbi.AttrTableBase);
        int m1 = (int)cx.ReadSlot(WasmAbi.AttrTableMask);
        Assert.Equal(v1, LookupInImage(cx, b1, m1, home, 1));
        Assert.Equal(v1, LookupInImage(cx, b1, m1, home, 2));
    }
}
