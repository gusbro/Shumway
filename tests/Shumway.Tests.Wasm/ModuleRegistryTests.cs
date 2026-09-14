using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Many modules in one engine: one per predicate, the grain the
/// browser's lazy mode compiles at. The registry decides which module a
/// functor runs in; the emitted code reaches a foreign functor through the
/// resume table and a sibling of its own module by a baked jump, so a
/// takeover or an eviction that moves a functor out of a module takes the
/// module's baked callers of it along (they re-promote against the live
/// code) and touches no module already built.</summary>
public sealed class ModuleRegistryTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- public lo/1.
        :- public hi/1.
        :- public both/1.
        :- public chase/2.
        lo(1). lo(2). lo(3).
        hi(X) :- lo(X), X > 1.
        both(X) :- lo(X).
        both(X) :- hi(X).
        chase(X, Y) :- both(X), hi(Y), Y >= X.
        solo(X) :- X = 1.
        """;

    private const string Goal = "findall(X-Y, chase(X, Y), L), length(L, N).";

    private static string Answer(PrologEngine e)
    {
        var r = e.Query(Goal);
        Assert.True(r.Success, "the goal failed");
        var parts = new List<string>();
        foreach (var (name, value) in r.Bindings) parts.Add($"{name}={value}");
        parts.Sort(System.StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    private static string Oracle()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        return Answer(plain);
    }

    private static int Fid(PrologEngine engine, string name, int arity)
    {
        foreach (var (_, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (aid, ar) = FunctorTable.Lookup(pred.FunctorId);
            if (ar == arity && (AtomTable.GetById(aid)?.Name ?? "").EndsWith(name))
                return pred.FunctorId;
        }
        throw new Xunit.Sdk.XunitException($"{name}/{arity} is not a static predicate");
    }

    /// <summary>One module per predicate answers what Tier-0 answers, and
    /// every crossing is a hop: nothing goes out to the host, nothing deopts.
    /// The counter-proof is the same corpus as ONE module, which cannot hop
    /// because it has no sibling to hop to.</summary>
    [Fact]
    public void OneModulePerPredicateAnswersLikeTier0AndOnlyHops()
    {
        string oracle = Oracle();
        var (engine, members, world) = TieredEngine.BuildWithWorld(Corpus);
        Assert.Equal(oracle, Answer(engine));       // promotes as it calls
        // ANTI-VACUITY: the four predicates each got a module.
        Assert.True(members.Count >= 4, $"only {members.Count} promoted");
        Assert.Equal(members.Count, world.Modules.ModuleCount);

        WasmTierDelegate.ResetDiag();
        Assert.Equal(oracle, Answer(engine));
        o.WriteLine($"per predicate: entries={WasmTierDelegate.DiagEntries} "
            + $"hops={WasmTierDelegate.DiagInWasmHops} switches={WasmTierDelegate.DiagSwitches} "
            + $"foreign={WasmTierDelegate.DiagForeignExits} deopts={WasmTierDelegate.DiagDeopts}");
        Assert.True(WasmTierDelegate.DiagEntries > 0, "nothing entered the tier");
        Assert.True(WasmTierDelegate.DiagInWasmHops > 0, "no module ever hopped");
        Assert.Equal(0, WasmTierDelegate.DiagSwitches);
        Assert.Equal(0, WasmTierDelegate.DiagForeignExits);
        Assert.Equal(0, WasmTierDelegate.DiagDeopts);

        // Counter-proof: one batch module, same corpus, same answer, no hop.
        var (batch, batchWorld) = OneModule();
        WasmTierDelegate.ResetDiag();
        Assert.Equal(oracle, Answer(batch));
        Assert.Equal(1, batchWorld.Modules.ModuleCount);
        Assert.True(WasmTierDelegate.DiagEntries > 0, "the batch never entered the tier");
        Assert.Equal(0, WasmTierDelegate.DiagInWasmHops);
    }

    /// <summary>The corpus as one module, the way a batch installs it.</summary>
    private static (PrologEngine Engine, DesktopWasmWorld World) OneModule()
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        engine.ConsultString(Corpus);
        engine.Query("true.");
        var world = new DesktopWasmWorld();
        var env = new EngineWasmCompileEnv();
        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (aid, _) = FunctorTable.Lookup(pred.FunctorId);
            string name = AtomTable.GetById(aid)?.Name ?? "";
            if (name.EndsWith("lo") || name.EndsWith("hi") || name.EndsWith("both")
                || name.EndsWith("chase") || name.EndsWith("solo"))
                members.Add(new WasmGroupMember(pred, addr, null));
        }
        TieredEngine.Install(world, members, env);
        foreach (var m in members)
            store.RegisterBoundDelegate(m.Predicate.FunctorId,
                new WasmTierDelegate(m.Predicate.FunctorId, world).Invoke);
        return (engine, world);
    }

    /// <summary>Installing a functor a second time takes it over: its rows
    /// move to the new module, and the old module's members that reach it
    /// by baked jumps -- hi/1 and both/1 call lo/1, chase/2 calls them --
    /// are displaced with it, since their jumps would land in the dead
    /// code. A member that never calls it stays. Nothing is rebuilt.</summary>
    [Fact]
    public void ATakeoverDisplacesTheBakedCallersAndTheOthersStay()
    {
        string oracle = Oracle();
        var (engine, world) = OneModule();
        int lo = Fid(engine, "lo", 1), hi = Fid(engine, "hi", 1), both = Fid(engine, "both", 1),
            chase = Fid(engine, "chase", 2), solo = Fid(engine, "solo", 1);
        Assert.Equal(0, world.Modules.OwnerOf(lo)!.Id);
        Assert.Equal(0, world.Modules.OwnerOf(chase)!.Id);
        Assert.Equal(0, world.Modules.OwnerOf(solo)!.Id);

        // lo/1 alone, again, as a module of its own.
        var env = new EngineWasmCompileEnv();
        WasmGroupMember? loMember = null;
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
            if (pred.FunctorId == lo) loMember = new WasmGroupMember(pred, addr, null);
        TieredEngine.Install(world, new List<WasmGroupMember> { loMember! }, env,
            store: null, out var displaced);

        Assert.Equal(new HashSet<int> { hi, both, chase }, new HashSet<int>(displaced));
        Assert.Equal(2, world.Modules.ModuleCount);
        Assert.Equal(1, world.Modules.OwnerOf(lo)!.Id);
        Assert.Equal(0, world.Modules.OwnerOf(solo)!.Id);
        Assert.Null(world.Modules.OwnerOf(chase));
        Assert.True(world.TryResolve(lo, 0, out var t) && t.ModuleId == 1,
            "lo/1's fresh entry did not move to the new module");
        Assert.False(world.TryResolve(chase, 0, out _), "chase/2 kept a row");
        Assert.True(world.TryResolve(solo, 0, out var s) && s.ModuleId == 0,
            "solo/1 did not survive the takeover");

        // The displaced run on bytecode until re-promoted (this engine has
        // no wasm store to re-promote them, so bytecode it is) and reach
        // lo/1 in its new module from their bytecode call sites; nothing
        // deopts.
        foreach (int fid in displaced) engine.IlPromotion.EvictDelegate(fid);
        WasmTierDelegate.ResetDiag();
        Assert.Equal(oracle, Answer(engine));
        Assert.True(WasmTierDelegate.DiagEntries > 0, "nothing entered the tier");
        Assert.Equal(0, WasmTierDelegate.DiagSwitches);
        Assert.Equal(0, WasmTierDelegate.DiagDeopts);
    }

    /// <summary>An eviction takes the baked callers along, transitively,
    /// and reports the whole set; the counter-proof evicts a member nobody
    /// calls and gets that member alone.</summary>
    [Fact]
    public void AnEvictionDragsTheBakedCallersAlong()
    {
        string oracle = Oracle();
        var (engine, world) = OneModule();
        int lo = Fid(engine, "lo", 1), hi = Fid(engine, "hi", 1), both = Fid(engine, "both", 1),
            chase = Fid(engine, "chase", 2), solo = Fid(engine, "solo", 1);

        var gone = world.Evict(new[] { lo });
        Assert.Equal(new HashSet<int> { lo, hi, both, chase }, new HashSet<int>(gone));
        Assert.True(world.Contains(solo));
        Assert.False(world.Contains(chase));
        Assert.Equal(1, world.Modules.ModuleCount);
        foreach (int fid in gone) engine.IlPromotion.EvictDelegate(fid);
        Assert.Equal(oracle, Answer(engine));

        // Counter-proof: chase/2 has no caller in the module.
        var (engine2, world2) = OneModule();
        int chase2 = Fid(engine2, "chase", 2);
        var gone2 = world2.Evict(new[] { chase2 });
        Assert.Equal(new[] { chase2 }, gone2);
        Assert.True(world2.Contains(Fid(engine2, "lo", 1)));
        Assert.True(world2.Contains(Fid(engine2, "both", 1)));
    }

    /// <summary>An evicted functor resolves nowhere, in the registry and in
    /// the modules alike, and runs on bytecode: the answers do not change,
    /// the calls into it become foreign exits.</summary>
    [Fact]
    public void AnEvictedFunctorDegradesToBytecode()
    {
        string oracle = Oracle();
        var (engine, members, world) = TieredEngine.BuildWithWorld(Corpus);
        Assert.Equal(oracle, Answer(engine));       // promotes as it calls
        Assert.True(members.Count >= 4);
        int lo = Fid(engine, "lo", 1);
        Assert.True(world.Contains(lo));

        world.Evict(new[] { lo });
        engine.IlPromotion.EvictDelegate(lo);

        Assert.False(world.Contains(lo));
        Assert.False(world.TryResolve(lo, 0, out _));
        Assert.Equal(members.Count, world.Modules.ModuleCount);    // the module stays
        WasmTierDelegate.ResetDiag();
        Assert.Equal(oracle, Answer(engine));
        Assert.True(WasmTierDelegate.DiagEntries > 0, "nothing entered the tier");
        Assert.True(WasmTierDelegate.DiagForeignExits > 0,
            "a call into the evicted lo/1 never left wasm");
    }

    /// <summary>Two engines, the same program: the same markers name
    /// different code. Each engine resolves through its own table, so the
    /// interleaved queries cannot see each other's modules.</summary>
    [Fact]
    public void TwoEnginesInOneProcessResolveThroughTheirOwnTables()
    {
        string oracle = Oracle();
        var (a, _, worldA) = TieredEngine.BuildWithWorld(Corpus);
        var (b, _, worldB) = TieredEngine.BuildWithWorld(Corpus);
        Assert.Equal(oracle, Answer(a));            // promotes as it calls
        Assert.Equal(oracle, Answer(b));
        Assert.NotSame(worldA.Modules, worldB.Modules);
        int loA = Fid(a, "lo", 1), loB = Fid(b, "lo", 1);
        // Same functor id, same marker, two tables.
        Assert.Equal(loA, loB);
        Assert.True(worldA.TryResolve(loA, 0, out _) && worldB.TryResolve(loB, 0, out _));

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(oracle, Answer(a));
            Assert.Equal(oracle, Answer(b));
        }
        // Evicting in one engine leaves the other untouched.
        worldA.Evict(new[] { loA });
        Assert.False(worldA.Contains(loA));
        Assert.True(worldB.Contains(loB));
    }

    /// <summary>A module is compiled against the id its probes bake; the
    /// install refuses a module compiled for any other id, before anything
    /// enters the world.</summary>
    [Fact]
    public void InstallRefusesAModuleCompiledForAnotherId()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(Corpus);
        engine.Query("true.");
        var world = new DesktopWasmWorld();
        var env = new EngineWasmCompileEnv();
        WasmGroupMember? lo = null;
        int loFid = Fid(engine, "lo", 1);
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
            if (pred.FunctorId == loFid) lo = new WasmGroupMember(pred, addr, null);
        var members = new List<WasmGroupMember> { lo! };
        var addrMap = new Dictionary<int, int> { [loFid] = lo!.Bias };

        var wrong = WasmPredicateCompiler.CompileGroup(members, env,
            moduleId: world.NextModuleId + 1);
        var ex = Assert.Throws<WasmCompileException>(() => wrong.InstallInto(world, addrMap));
        Assert.Contains("compiled as id", ex.Message);
        Assert.Equal(0, world.Modules.ModuleCount);

        // Counter-proof: the right id installs.
        var right = WasmPredicateCompiler.CompileGroup(members, env,
            moduleId: world.NextModuleId);
        right.InstallInto(world, addrMap);
        Assert.Equal(1, world.Modules.ModuleCount);
    }

    /// <summary>Backtracking across modules a great many times: every retry
    /// is a hop, a tail call that replaces the frame. If a hop stacked, this
    /// would overflow long before the end.</summary>
    [Fact]
    public void DeepBacktrackingAcrossModulesHopsWithoutStacking()
    {
        const string corpus = """
            :- public nat/2.
            :- public upto/2.
            nat(N, N).
            nat(I, N) :- I1 is I + 1, nat(I1, N).
            upto(Limit, N) :- nat(0, N), N >= Limit, !.
            """;
        var (engine, members, _) = TieredEngine.BuildWithWorld(corpus);
        Assert.True(engine.Query("upto(3, 3).").Success);    // promotes as it calls
        Assert.True(members.Count >= 2, $"only {members.Count} promoted");
        WasmTierDelegate.ResetDiag();
        var r = engine.Query("upto(2000000, N).");
        Assert.True(r.Success);
        Assert.Equal("2000000", r["N"]!.ToString());
        o.WriteLine($"hops={WasmTierDelegate.DiagInWasmHops} entries={WasmTierDelegate.DiagEntries} "
            + $"builtins={WasmTierDelegate.DiagBuiltins} deopts={WasmTierDelegate.DiagDeopts}");
        Assert.True(WasmTierDelegate.DiagInWasmHops >= 2000000,
            $"the retries did not hop: {WasmTierDelegate.DiagInWasmHops}");
        Assert.Equal(0, WasmTierDelegate.DiagSwitches);
    }
}
