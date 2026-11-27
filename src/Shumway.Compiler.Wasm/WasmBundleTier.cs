using System;
using System.Collections.Generic;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;

namespace Shumway.Compiler.Wasm;

/// <summary>A bundle's wasm tier: <c>shumway-link --wasm</c> bakes the
/// bundle's static predicates into one relocatable module
/// (<see cref="WasmRelocatableModule"/>) and stores it in the bundle; a host
/// with a wasm world installs it when the bundle is loaded, in place of
/// compiling those predicates itself.
///
/// <para>The rules of the bundle's dynamic predicates take helper predicates
/// when an engine first links them (<c>'$disj_N'</c> and kin), as the IL
/// tier's bake of a dynamic predicate takes the helpers it reaches. They are
/// baked into a second module, under names every process gives them
/// (<see cref="WasmPortableNames"/>). Apart, so that an engine that has not
/// one of those rules as it was baked refuses that module alone.</para>
///
/// <para>Each dynamic predicate the bundle gives clauses to is baked as its
/// snapshot (ADR-054), a module of its own after those two, as the IL tier
/// bakes it (ADR-023). A host holds it until the predicate is first called,
/// and installs it then over the shadow region it links for the snapshot,
/// when the predicate's clauses are the ones it was compiled from.</para></summary>
public static class WasmBundleTier
{
    /// <summary>Bakes the bundle's own static predicates: the bundle is
    /// loaded into a fresh engine (as the stdlib itself when <paramref
    /// name="stdlib"/>, else on top of the ordinary prelude) and every
    /// static it added is compiled with relocations. The modules to store
    /// in the bundle: none when nothing compiled. Members the compiler
    /// refuses are dropped and reported through <paramref name="log"/>.
    ///
    /// <para>The libraries an archive imports and does not hold (ADR-038
    /// §4) load first, through the library path <paramref name="prepare"/>
    /// gives the engine, and are not baked: a call into one is a relocation
    /// the install resolves against their code.</para></summary>
    public static IReadOnlyList<byte[]> Bake(Bundle bundle, bool stdlib, Action<string>? log = null,
                                             Action<PrologEngine>? prepare = null)
    {
        if (!stdlib) return new WasmBakeSession(prepare).Bake(bundle, log);
        var engine = PrologEngine.FromBundle(bundle);
        Silence(engine);
        return BakeLoaded(engine, new HashSet<int>(), new Dictionary<int, string>(), log);
    }

    /// <summary>Bakes what the last bundle loaded into <paramref name="engine"/>
    /// added, but for what is in <paramref name="before"/>: its static
    /// predicates, in a module of their own the helpers of its dynamic
    /// predicates' rules, and then the snapshot of each dynamic predicate
    /// whose clauses are not the ones of <paramref name="dynamicsBefore"/>
    /// (<see cref="WasmPromotionStore.DynamicDigestsOf"/>).</summary>
    internal static IReadOnlyList<byte[]> BakeLoaded(PrologEngine engine, HashSet<int> before,
        Dictionary<int, string> dynamicsBefore, Action<string>? log)
    {
        // What the bundle registered, read before the query that links: that
        // setup mints the helpers of the bundle's dynamic rules (a library's
        // attribute_goals/4 hook), under numbers of this engine's counter.
        var own = new HashSet<int>(engine.PrecompiledStaticPredicates.Keys);
        engine.Query("true.");
        var store = engine.IlPromotion;
        var portable = new WasmPortableNames(engine);
        var members = new List<WasmGroupMember>();
        var helpers = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            int fid = pred.FunctorId;
            if (before.Contains(fid)) continue;
            bool mine = own.Contains(fid);
            if (!mine && !portable.Names(fid)) continue;
            (mine ? members : helpers).Add(new WasmGroupMember(pred, addr,
                store.FloatPoolProvider?.Invoke(fid), store.ImagePoolProvider?.Invoke(fid)));
        }
        var modules = new List<byte[]>(2);
        // What the compiler refuses goes with the module, under the name the
        // module would carry for it: the host that installs the module does
        // not offer those to its own batch.
        var refused = new List<(string, int, string)>();
        Func<string, string>? carried = null;
        void Skipping(WasmGroupMember m, string why)
        {
            var (name, arity) = RelocatingCompileEnv.NameOf(m.Predicate.FunctorId);
            log?.Invoke($"wasm: skipping {name}/{arity}: {why}");
            refused.Add((carried is null ? name : carried(name), arity, why));
        }
        if (members.Count > 0)
        {
            var module = WasmRelocatableModule.BakeCompilable(members, new EngineWasmCompileEnv(),
                Skipping, log);
            if (module.Members.Count > 0)
            {
                log?.Invoke($"wasm: {module.Members.Count} of {members.Count} predicates baked");
                modules.Add(module.WithRefused(refused).ToBytes());
            }
        }
        if (helpers.Count > 0)
        {
            refused = new List<(string, int, string)>();
            carried = portable.ToPortable;
            var module = WasmRelocatableModule.BakeCompilable(helpers, new EngineWasmCompileEnv(),
                Skipping, log, portable.ToPortable);
            if (module.Members.Count > 0)
            {
                log?.Invoke($"wasm: {module.Members.Count} of {helpers.Count} helpers of dynamic rules baked");
                modules.Add(module.WithRefused(refused).ToBytes());
            }
        }
        // A snapshot the compiler refuses is found out by the predicate's
        // own promotion, which is not a batch's.
        refused = new List<(string, int, string)>();
        carried = null;
        int dynamics = 0, snapshots = 0;
        foreach (var (fid, digest) in WasmPromotionStore.DynamicDigestsOf(engine))
        {
            if (dynamicsBefore.TryGetValue(fid, out var was) && was == digest) continue;
            dynamics++;
            if (WasmPromotionStore.DynamicSnapshotOf(engine, fid) is not { } snapshot) continue;
            // Compiled from the snapshot's own bytecode, which is linked
            // nowhere until an engine calls the predicate: every site in it
            // is an offset, so any base serves.
            var member = new WasmGroupMember(snapshot, SnapshotBakeBase,
                store.FloatPoolProvider?.Invoke(fid), store.ImagePoolProvider?.Invoke(fid));
            var module = WasmRelocatableModule.BakeCompilable(new[] { member },
                new EngineWasmCompileEnv(), Skipping, null, portable.ToPortable);
            if (module.Members.Count == 0) continue;
            modules.Add(module.AsSnapshot(digest).ToBytes());
            snapshots++;
        }
        if (dynamics > 0)
            log?.Invoke($"wasm: {snapshots} of {dynamics} dynamic predicates baked as snapshots");
        return modules;
    }

    private const int SnapshotBakeBase = 1 << 16;

    /// <summary>Whether every wasm module <paramref name="bundle"/> carries
    /// is one this engine installs (<see cref="WasmRelocatableModule.IsCurrent"/>).
    /// True for a bundle with none.</summary>
    public static bool ModulesAreCurrent(Bundle bundle)
    {
        foreach (var module in bundle.WasmModules)
            if (!WasmRelocatableModule.IsCurrent(module)) return false;
        return true;
    }

    // A snapshot's module waits for its predicate's first call: the shadow
    // region it resolves against is linked then, in the goal's code.
    private static (IReadOnlyList<int> Installed, string Note) Hold(
        PrologEngine engine, IWasmExecutionWorld world, WasmRelocatableModule module)
    {
        var store = engine.IlPromotion;
        var wasm = store.Wasm!;
        var member = module.Members[0];
        int fid = FunctorTable.Intern(AtomTable.Intern(member.Name, permanent: true).Id, member.Arity);
        var portable = new WasmPortableNames(engine);
        wasm.HoldBakedSnapshot(fid, module.SnapshotDigest, SnapshotInstaller(wasm, world, module, fid, portable));
        return (Array.Empty<int>(), $"the snapshot of {member.Name}/{member.Arity} is held");
    }

    // Installs a snapshot's module over the shadow region an engine gave the
    // predicate: given the snapshot's bytecode and that address, the delegate,
    // or null when the module does not fit that code.
    private static Func<CompiledPredicate, int, PredicateDelegate?> SnapshotInstaller(
        WasmPromotionStore wasm, IWasmExecutionWorld world, WasmRelocatableModule module, int fid,
        WasmPortableNames portable)
        => (snapshot, address) =>
        {
            if (!module.TryResolve(new EngineWasmCompileEnv(),
                    f => f == fid ? address : -1,
                    _ => WasmRelocatableModule.ShapeOf(snapshot, portable.ToPortable),
                    world.NextModuleId, out var entry, out var entryAddr, out _,
                    portable.ToLive))
                return null;
            try
            {
                var displaced = entry.InstallInto(world, entryAddr);
                if (displaced.Count > 0) wasm.Displaced(displaced);
            }
            catch (WasmCompileException)
            {
                return null;
            }
            return new WasmTierDelegate(fid, world).Invoke;
        };

    /// <summary>Compiles the snapshot of a dynamic predicate away from the
    /// engine's thread (ADR-054, amendment): the shape of
    /// <see cref="WasmPromotionStore.SnapshotCompiler"/>. Called on the
    /// engine's thread with the snapshot the predicate has now;
    /// <paramref name="run"/> takes the compile to wherever the host compiles
    /// aside, and <paramref name="done"/> is called from there with what
    /// installs the module (as a bundle's snapshot is installed), or with
    /// why there is none. The module is compiled from the snapshot's own
    /// bytecode, which is linked nowhere: it names no address and no module
    /// id, so it fits whatever shadow region the predicate has when the
    /// module is installed, and the next one after the code space is built
    /// again.</summary>
    public static void CompileSnapshotAside(PrologEngine engine, IWasmExecutionWorld world,
        CompiledPredicate snapshot, string digest,
        Action<Func<CompiledPredicate, int, PredicateDelegate?>?, string?> done, Action<Action> run)
    {
        var store = engine.IlPromotion;
        var wasm = store.Wasm
            ?? throw new InvalidOperationException("the engine has no wasm store");
        int fid = snapshot.FunctorId;
        var portable = new WasmPortableNames(engine).Loaded();
        // The literals by value, taken with the snapshot on this thread: the
        // pools may be swept while the module compiles.
        var member = new WasmGroupMember(snapshot, SnapshotBakeBase,
            snapshot.LiteralView is { } floats ? floats.FloatView : store.FloatPoolProvider?.Invoke(fid),
            snapshot.LiteralView is { } images ? images.ImageView : store.ImagePoolProvider?.Invoke(fid));
        run(() =>
        {
            try
            {
                string? refusal = null;
                var module = WasmRelocatableModule.BakeCompilable(new[] { member },
                    new EngineWasmCompileEnv(), (_, why) => refusal = why, null, portable.ToPortable);
                if (module.Members.Count == 0)
                    done(null, refusal ?? "the compiler refused it");
                else
                    done(SnapshotInstaller(wasm, world, module.AsSnapshot(digest), fid, portable), null);
            }
            catch (Exception e)
            {
                done(null, e.Message);
            }
        });
    }

    // No IL promotion in the baking engine: its work would be wasted, and
    // a wasm store with an unreachable threshold gives StaticPredicatesOf
    // its link without promoting anything either.
    internal static void Silence(PrologEngine engine)
    {
        var store = engine.IlPromotion;
        store.Threshold = 0;
        store.Wasm = new WasmPromotionStore(store) { Threshold = int.MaxValue };
    }

    /// <summary>Installs a bundle's module into <paramref name="world"/>
    /// and binds the tier's delegates, against the engine's current static
    /// link, or against <paramref name="linked"/> (address to predicate)
    /// when a running goal linked the bundle into its own code. The shape of
    /// <see cref="WasmPromotionStore.BundleInstaller"/>: the functors
    /// installed, with a note saying so or why not. Requires an engine with
    /// a wasm store, linked when <paramref name="linked"/> is null.</summary>
    public static (IReadOnlyList<int> Installed, string Note) Install(
        PrologEngine engine, IWasmExecutionWorld world, byte[] bytes,
        IReadOnlyDictionary<int, CompiledPredicate>? linked = null)
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
        if (module.IsSnapshot) return Hold(engine, world, module);
        var biasByFid = new Dictionary<int, int>();
        var predByFid = new Dictionary<int, CompiledPredicate>();
        if (linked is null)
            foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
            {
                biasByFid[pred.FunctorId] = addr;
                predByFid[pred.FunctorId] = pred;
            }
        else
            foreach (var (addr, pred) in linked)
            {
                biasByFid[pred.FunctorId] = addr;
                predByFid[pred.FunctorId] = pred;
            }
        // A module of helpers names them as every process does; this
        // engine's names for them are asked for only then.
        var portable = module.CarriesPortableNames ? new WasmPortableNames(engine) : null;
        if (!module.TryResolve(new EngineWasmCompileEnv(),
                fid => biasByFid.TryGetValue(fid, out int b) ? b : -1,
                fid => WasmRelocatableModule.ShapeOf(predByFid[fid],
                    portable is null ? null : portable.ToPortable),
                world.NextModuleId, out var entry, out var entryAddr, out string reason,
                portable is null ? null : portable.ToLive))
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
        // The module's builtins have the forms the bake saw, so what the
        // compiler refused there it refuses here.
        foreach (var (name, arity, why) in module.Refused)
        {
            string? local = portable is null ? name : portable.ToLive(name);
            if (local is null) continue;
            int fid = FunctorTable.Intern(AtomTable.Intern(local, permanent: true).Id, arity);
            if (biasByFid.ContainsKey(fid)) wasm.MarkUnpromotable(fid, why);
        }
        return (installed, $"{installed.Count} predicates installed from the bundle");
    }
}

/// <summary>A baking engine prepared once for several bundles: the libraries of
/// one on-demand compile, each baked as it is compiled. Each bake loads into
/// the engine what the bundle imports and is not there yet, then the bundle,
/// and bakes the predicates the bundle added. A fresh engine per bundle
/// installed the prelude and loaded every library under it again for each.</summary>
public sealed class WasmBakeSession
{
    private readonly PrologEngine _engine;

    public WasmBakeSession(Action<PrologEngine>? prepare = null)
    {
        _engine = new PrologEngine();
        WasmBundleTier.Silence(_engine);
        prepare?.Invoke(_engine);
    }

    /// <summary>Bakes the bundle's own static predicates and the helpers of
    /// its dynamic rules (<see cref="WasmBundleTier.BakeLoaded"/>); no module
    /// when none compiled. The bundle's initialization goals do not run: what
    /// they assert is compiled where the bundle loads.</summary>
    public IReadOnlyList<byte[]> Bake(Bundle bundle, Action<string>? log = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _engine.LoadLibraryDependencies(bundle);
        _engine.Query("true.");
        var before = new HashSet<int>();
        foreach (var (_, pred) in WasmPromotionStore.StaticPredicatesOf(_engine))
            before.Add(pred.FunctorId);
        var dynamicsBefore = WasmPromotionStore.DynamicDigestsOf(_engine);
        double dependencies = clock.Elapsed.TotalSeconds;
        _engine.LoadBundleWithoutInitialization(bundle);
        log?.Invoke($"wasm: loaded what it imports in {dependencies:0.00} s, "
            + $"the bundle in {clock.Elapsed.TotalSeconds - dependencies:0.00} s");
        return WasmBundleTier.BakeLoaded(_engine, before, dynamicsBefore, log);
    }
}
