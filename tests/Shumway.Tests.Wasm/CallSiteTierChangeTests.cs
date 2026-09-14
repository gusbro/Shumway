using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>The linker bakes the tier's state into the persistent program:
/// a callee that can never promote gets CallBytecode sites (no dispatch
/// hook), one with a delegate gets CallIl sites (no bytecode). Both are
/// wrong the moment the tier changes between queries -- attached after a
/// query ran, or a delegate evicted by a relink -- and the program stays
/// linked until the next consult. A tier that turns on relinks; a CallIl
/// site that lost its delegate heals into the Call it was.</summary>
public sealed class CallSiteTierChangeTests
{
    private const string Corpus = """
        lo(1). lo(2). lo(3).
        chase(X) :- lo(X), X > 1.
        """;

    private const string Goal = "findall(X, chase(X), L).";

    private static string Answer(PrologEngine e)
    {
        var r = e.Query(Goal);
        Assert.True(r.Success, "the goal failed");
        foreach (var (name, value) in r.Bindings)
            if (name == "L") return value.ToString()!;
        throw new Xunit.Sdk.XunitException("no L");
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

    /// <summary>lo/1 alone as a module; chase/1 stays bytecode, so its
    /// call site is the only way into the tier.</summary>
    private static DesktopWasmWorld PromoteLo(PrologEngine engine)
    {
        var store = engine.IlPromotion;
        var world = new DesktopWasmWorld();
        var env = new EngineWasmCompileEnv();
        var members = new List<WasmGroupMember>();
        int lo = Fid(engine, "lo", 1);
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
            if (pred.FunctorId == lo) members.Add(new WasmGroupMember(pred, addr, null));
        Assert.Single(members);
        TieredEngine.Install(world, members, env);
        foreach (var m in members)
        {
            store.RegisterBoundDelegate(m.Predicate.FunctorId,
                new WasmTierDelegate(m.Predicate.FunctorId, world).Invoke);
            store.Wasm?.NoteInstalled(m.Predicate.FunctorId, m.Bias, m.Predicate);
        }
        if (store.Wasm is { } w) w.StaleEvicted = world.Evict;
        return world;
    }

    /// <summary>A lazy-mode store that promotes nothing by itself: the
    /// tests install what they want by hand.</summary>
    private static void Attach(PrologEngine engine)
    {
        var store = engine.IlPromotion;
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = 1,
            Promoter = (_, _) => null,
        };
    }

    private static long EntriesOf(PrologEngine engine, string oracle)
    {
        WasmTierDelegate.ResetDiag();
        Assert.Equal(oracle, Answer(engine));
        Assert.Equal(0, WasmTierDelegate.DiagDeopts);
        return WasmTierDelegate.DiagEntries;
    }

    [DiagTheory]
    [InlineData("before")]   // the boot: attached before any query linked
    [InlineData("after")]    // wasm_compile. on a live engine that already ran queries
    [InlineData("never")]    // no store at all: a delegate bound by hand
    public void ADelegateIsReachedFromSitesLinkedBeforeTheTierExisted(string when)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        string oracle = Answer(plain);

        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(Corpus);
        if (when == "before") Attach(engine);
        Assert.Equal(oracle, Answer(engine));    // links chase/1's site
        if (when == "after") Attach(engine);
        PromoteLo(engine);
        Assert.True(EntriesOf(engine, oracle) > 0,
            "chase/1's call site never reached lo/1's module");
    }

    [DiagFact]
    public void TurningTheTierOffMakesTheSitesBytecodeAgain()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        string oracle = Answer(plain);

        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(Corpus);
        Attach(engine);
        engine.Query("true.");
        PromoteLo(engine);
        Assert.True(EntriesOf(engine, oracle) > 0, "the tier was never entered");

        // wasm_compile(none): promotion off, the installed keep running.
        engine.IlPromotion.Wasm!.Threshold = 0;
        Assert.True(EntriesOf(engine, oracle) > 0,
            "an installed delegate stopped running when promotion went off");
        // ...until they are evicted; then the relinked sites are bytecode.
        engine.IlPromotion.EvictDelegate(Fid(engine, "lo", 1));
        Assert.Equal(0, EntriesOf(engine, oracle));
    }

    /// <summary>The lazy mode's relink: the tick evicts the redefined
    /// delegate AFTER the throwaway query rewrote its sites to CallIl, and
    /// nothing recompiles it. Before the healing this threw "CallIl: no IL
    /// delegate ... invariant violated" at the first call.</summary>
    [DiagFact]
    public void ARelinkEvictionAfterTheSitesWereRewrittenHeals()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        plain.ConsultString("lo(4).");
        string oracle = Answer(plain);

        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(Corpus);
        Attach(engine);
        engine.Query("true.");
        PromoteLo(engine);
        Assert.True(engine.Query(Goal).Success);

        engine.ConsultString("lo(4).");             // lo/1's code changes
        var wasm = engine.IlPromotion.Wasm!;
        wasm.CompileAllTick(engine);
        Assert.Equal(1, wasm.RelinkEvictions);
        Assert.Null(engine.IlPromotion.TryGet(Fid(engine, "lo", 1)));

        // Healed: the answer is the live one, on bytecode.
        Assert.Equal(0, EntriesOf(engine, oracle));
        // And the healed site promotes again like any other.
        PromoteLo(engine);
        Assert.True(EntriesOf(engine, oracle) > 0, "the healed site never re-entered the tier");
    }

    /// <summary>The same eviction with no relink at all: the CallIl sites
    /// of the running program lose their delegate between two queries.</summary>
    [DiagFact]
    public void AnEvictionBetweenQueriesHeals()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        string oracle = Answer(plain);

        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(Corpus);
        Attach(engine);
        engine.Query("true.");
        PromoteLo(engine);
        Assert.True(EntriesOf(engine, oracle) > 0, "the tier was never entered");
        engine.IlPromotion.EvictDelegate(Fid(engine, "lo", 1));
        Assert.Equal(0, EntriesOf(engine, oracle));
    }
}
