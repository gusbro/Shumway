using System.Reflection;
using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A baked module is compiled through <see cref="RelocatingCompileEnv"/>,
/// and every inline form the live compile gives a builtin has to reach it:
/// the interface answers "no" by default, so a form added upstream and not
/// delegated there leaves every baked library (the prelude, clp(Z)) on the
/// slow path while the live compile keeps it. Eleven forms were lost that way.
/// </summary>
public sealed class RelocatingCompileEnvTests(ITestOutputHelper o)
{
    [Fact]
    public void EveryMemberOfTheInterfaceIsImplementedNotInherited()
    {
        var map = typeof(RelocatingCompileEnv).GetInterfaceMap(typeof(IWasmCompileEnv));
        var inherited = new List<string>();
        for (int i = 0; i < map.TargetMethods.Length; i++)
            if (map.TargetMethods[i].DeclaringType != typeof(RelocatingCompileEnv))
                inherited.Add(map.InterfaceMethods[i].Name);
        Assert.True(inherited.Count == 0,
            "falls back to the interface's default in a bake: " + string.Join(", ", inherited));
        // Anti-vacuity: the interface does have defaults to fall back to.
        Assert.Contains(typeof(IWasmCompileEnv).GetMethods(), m => !m.IsAbstract);
    }

    private const string Corpus = """
        fl(0, _, _) :- !.
        fl(N, G, Tr) :- functor(f(a, b), F, A), F == f, A == 2, arg(1, f(x, y), X), X == x,
                 T =.. [g, 1], T == g(1), ground(T), acyclic_term(T),
                 sort([3, 1, 2], S), S == [1, 2, 3], b_getval(k, V), V == 1,
                 call(G, 3), call(Tr),
                 N1 is N - 1, fl(N1, G, Tr).
        go(N) :- b_setval(k, 1), fl(N, integer, true).
        """;

    private static readonly string[] Inline =
        { "functor/3", "arg/3", "=../2", "ground/1", "acyclic_term/1", "sort/2",
          "b_getval/2", "call/2", "call/1", "true/0" };

    private static (PrologEngine Engine, List<WasmGroupMember> Members) Linked(string prefix)
    {
        var engine = new PrologEngine();
        engine.ConsultString(prefix + Corpus);
        var store = engine.IlPromotion;
        store.Threshold = 0;
        store.Wasm = new WasmPromotionStore(store) { Threshold = int.MaxValue };
        engine.Query("true.");
        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (name, _) = RelocatingCompileEnv.NameOf(pred.FunctorId);
            if (name is "user$fl" or "user$go")
                members.Add(new WasmGroupMember(pred, addr,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId)));
        }
        Assert.Equal(2, members.Count);
        return (engine, members);
    }

    private static WasmRelocatableModule Baked()
    {
        var (_, members) = Linked("");
        var module = WasmRelocatableModule.Bake(members, new EngineWasmCompileEnv());
        return WasmRelocatableModule.Read(new MemoryStream(module.ToBytes()));
    }

    /// <summary>Installs the module into an engine whose link differs from the
    /// bake's (a filler moves every base) and binds the tier.</summary>
    private static PrologEngine Relocated(WasmRelocatableModule module, out string reason)
    {
        var (engine, members) = Linked("filler(1). filler(2). filler(3).\n");
        var world = new DesktopWasmWorld();
        var biasByFid = new Dictionary<int, int>();
        foreach (var m in members) biasByFid[m.Predicate.FunctorId] = m.Bias;
        if (!module.TryResolve(new EngineWasmCompileEnv(),
                fid => biasByFid.TryGetValue(fid, out int b) ? b : -1,
                world.NextModuleId, out var entry, out var entryAddr, out reason))
            return null!;
        entry.InstallInto(world, entryAddr);
        foreach (var m in members)
        {
            engine.IlPromotion.RegisterBoundDelegate(m.Predicate.FunctorId,
                new WasmTierDelegate(m.Predicate.FunctorId, world).Invoke);
            engine.IlPromotion.Wasm!.NoteInstalled(m.Predicate.FunctorId, m.Bias, m.Predicate);
        }
        return engine;
    }

    [Fact]
    public void TheBakeRecordsTheFormsAndTheInstallReChecksThem()
    {
        var module = Baked();
        WasmBuiltinEvidence Of(string name, int arity)
            => Assert.Single(module.Builtins, b => b.Name == name && b.Arity == arity);
        Assert.True(Of("functor", 3).Forms.HasFlag(WasmInlineForms.Functor));
        Assert.True(Of("sort", 2).Forms.HasFlag(WasmInlineForms.Sort));
        Assert.True(Of("b_getval", 2).Forms.HasFlag(WasmInlineForms.GlobalFetchRaises));
        Assert.True(Of("call", 2).Forms.HasFlag(WasmInlineForms.MetaCallN));
        Assert.Equal(1, Of("call", 2).MetaCallAppended);

        var engine = Relocated(module, out string reason);
        Assert.True(engine is not null, reason);
        Assert.True(engine!.Query("go(20).").Success);

        // A form the installing engine does not give is a module it refuses.
        var ms = new MemoryStream();
        module.Write(ms, module.Relocations, module.Builtins.Select(b =>
            b.Name == "sort" ? b with { Forms = b.Forms & ~WasmInlineForms.Sort } : b).ToList());
        ms.Position = 0;
        var tampered = WasmRelocatableModule.Read(ms);
        Assert.Null(Relocated(tampered, out reason));
        Assert.Contains("sort/2", reason);
    }

    private static Dictionary<string, long> Exits(PrologEngine engine)
    {
        Assert.True(engine.Query("go(3).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("go(200).").Success);
        Assert.True(WasmTierDelegate.DiagEntries > 0, "the module never ran");
        var exits = new Dictionary<string, long>();
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            exits[$"{n}/{a}"] = hits;
        return exits;
    }

    /// <summary>The property itself: the baked module leaves for the same
    /// builtins, as often, as the live compile of the same code.</summary>
    [DiagFact]
    public void ABakedModuleExitsExactlyWhereTheLiveCompileDoes()
    {
        var (live, _) = TieredEngine.Build(Corpus);
        var liveExits = Exits(live);
        var baked = Relocated(Baked(), out string reason);
        Assert.True(baked is not null, reason);
        var bakedExits = Exits(baked!);
        foreach (var (k, v) in liveExits) o.WriteLine($"live  {k} x{v}");
        foreach (var (k, v) in bakedExits) o.WriteLine($"baked {k} x{v}");

        // Anti-vacuity: live, these are inline forms, so a bake that lost
        // them shows as exits the live compile does not make.
        foreach (string b in Inline)
            Assert.False(liveExits.GetValueOrDefault(b) >= 200, $"{b} is not inline even live");
        foreach (string b in liveExits.Keys.Union(bakedExits.Keys))
            Assert.True(liveExits.GetValueOrDefault(b) == bakedExits.GetValueOrDefault(b),
                $"{b}: {liveExits.GetValueOrDefault(b)} exits live, "
                + $"{bakedExits.GetValueOrDefault(b)} baked");
    }
}
