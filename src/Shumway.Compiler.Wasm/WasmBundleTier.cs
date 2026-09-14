using System;
using System.Collections.Generic;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;

namespace Shumway.Compiler.Wasm;

/// <summary>A bundle's wasm tier: <c>shumway-link --wasm</c> bakes the
/// bundle's static predicates into one relocatable module
/// (<see cref="WasmRelocatableModule"/>) and stores it in the bundle; a host
/// with a wasm world installs it when the bundle is loaded, in place of
/// compiling those predicates itself.</summary>
public static class WasmBundleTier
{
    /// <summary>Bakes the bundle's own static predicates: the bundle is
    /// loaded into a fresh engine (as the stdlib itself when <paramref
    /// name="stdlib"/>, else on top of the ordinary prelude) and every
    /// static it added is compiled with relocations. Null when nothing
    /// compiled. Members the compiler refuses are dropped and reported
    /// through <paramref name="log"/>.</summary>
    public static byte[]? Bake(Bundle bundle, bool stdlib, Action<string>? log = null)
    {
        PrologEngine engine;
        var before = new HashSet<int>();
        if (stdlib)
        {
            engine = PrologEngine.FromBundle(bundle);
            Silence(engine);
        }
        else
        {
            engine = new PrologEngine();
            Silence(engine);
            engine.Query("true.");
            foreach (var (_, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
                before.Add(pred.FunctorId);
            engine.LoadBundle(bundle);
        }
        // What the bundle registered, read BEFORE the query that links: that
        // setup mints helpers for the bundle's dynamic clauses (a library's
        // attribute_goals/4 hook) under per-process numbers, and a member
        // the loading engine names differently refuses the whole module.
        var own = new HashSet<int>(engine.PrecompiledStaticPredicates.Keys);
        engine.Query("true.");
        var store = engine.IlPromotion;
        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            if (before.Contains(pred.FunctorId) || !own.Contains(pred.FunctorId)) continue;
            members.Add(new WasmGroupMember(pred, addr,
                store.FloatPoolProvider?.Invoke(pred.FunctorId)));
        }
        if (members.Count == 0) return null;
        var module = WasmRelocatableModule.BakeCompilable(members, new EngineWasmCompileEnv(),
            (m, why) =>
            {
                var (name, arity) = RelocatingCompileEnv.NameOf(m.Predicate.FunctorId);
                log?.Invoke($"wasm: skipping {name}/{arity}: {why}");
            });
        if (module.Members.Count == 0) return null;
        log?.Invoke($"wasm: {module.Members.Count} of {members.Count} predicates baked");
        return module.ToBytes();
    }

    // No IL promotion in the baking engine: its work would be wasted, and
    // a wasm store with an unreachable threshold gives StaticPredicatesOf
    // its link without promoting anything either.
    private static void Silence(PrologEngine engine)
    {
        var store = engine.IlPromotion;
        store.Threshold = 0;
        store.Wasm = new WasmPromotionStore(store) { Threshold = int.MaxValue };
    }

    /// <summary>Installs a bundle's module into <paramref name="world"/>
    /// against the engine's current static link and binds the tier's
    /// delegates. The shape of <see
    /// cref="WasmPromotionStore.BundleInstaller"/>: the functors installed,
    /// with a note saying so or why not. Requires a linked engine with a
    /// wasm store.</summary>
    public static (IReadOnlyList<int> Installed, string Note) Install(
        PrologEngine engine, IWasmExecutionWorld world, byte[] bytes)
    {
        var store = engine.IlPromotion;
        var wasm = store.Wasm
            ?? throw new InvalidOperationException("the engine has no wasm store");
        WasmRelocatableModule module;
        try
        {
            module = WasmRelocatableModule.Read(new System.IO.MemoryStream(bytes));
        }
        catch (System.IO.InvalidDataException e)
        {
            return (Array.Empty<int>(), "not installed: " + e.Message);
        }
        var biasByFid = new Dictionary<int, int>();
        var predByFid = new Dictionary<int, CompiledPredicate>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            biasByFid[pred.FunctorId] = addr;
            predByFid[pred.FunctorId] = pred;
        }
        if (!module.TryResolve(new EngineWasmCompileEnv(),
                fid => biasByFid.TryGetValue(fid, out int b) ? b : -1,
                fid => WasmRelocatableModule.ShapeOf(predByFid[fid]),
                world.NextModuleId, out var entry, out var entryAddr, out string reason))
            return (Array.Empty<int>(), "not installed: " + reason);
        try
        {
            var displaced = entry.InstallInto(world, entryAddr);
            if (displaced.Count > 0) wasm.Displaced(displaced);
        }
        // A browser's refusal to register is a WasmCompileException too.
        catch (WasmCompileException e)
        {
            return (Array.Empty<int>(), "not installed: " + e.Message);
        }
        var installed = new List<int>(entryAddr.Count);
        foreach (var (fid, bias) in entryAddr)
        {
            store.RegisterBoundDelegate(fid, new WasmTierDelegate(fid, world).Invoke);
            wasm.NoteInstalled(fid, bias, predByFid[fid]);
            installed.Add(fid);
        }
        return (installed, $"{installed.Count} predicates installed from the bundle");
    }
}
