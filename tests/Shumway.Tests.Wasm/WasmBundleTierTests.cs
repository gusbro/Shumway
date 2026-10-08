using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A bundle's wasm tier end to end: linked with a baker, loaded
/// into another process state, installed at the first link without
/// compiling, and running exactly as a live compile of the same
/// predicates does. A bundle without an installer keeps its module
/// pending; a module baked from other code is refused.</summary>
public class WasmBundleTierTests
{
    private const string Corpus = """
        color(red). color(green). color(blue).
        shape(circle(1)). shape(square(2)). shape(tri(3, 4)).
        pick(C, S) :- color(C), shape(S).
        sum([], 0).
        sum([H|T], N) :- sum(T, M), N is M + H.
        same(X, Y) :- X == Y.
        eq(X, Y) :- X = Y.
        big(X) :- X > 100.
        outside(X) :- helper(X).
        helper(z).
        main :- pick(_, _), sum([], _), same(a, a), eq(_, _), big(101), outside(_).
        """;

    private const string Probe = """
        findall(C-S, pick(C, S), L), sum([1,2,3], 6), same(a, a), \+ same(a, b),
        eq(f(X), f(1)), X == 1, big(101), \+ big(5), outside(z), \+ outside(y).
        """;

    // Consulted before the bundle in the loading engine: every linked base
    // differs from the baking engine's.
    private const string Filler = "filler(1, 2, 3). filler(4, 5, 6). filler(x, y, z).\n";

    private static LinkResult Link(Func<Bundle, byte[]?>? baker, bool stripWam = false)
        => ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Corpus, "user") },
            EntryPoints = new[] { new PredicateRef("main", 0) },
            WasmBaker = baker,
            IncludeCompiledIl = stripWam,
            StripWam = stripWam,
        });

    private static byte[]? Bake(Bundle b) => WasmBundleTier.Bake(b, stdlib: false);

    /// <summary>A dirty engine with a wasm store: IL promotion off, wasm
    /// promotion never (the bundle's module is the only wasm it gets).</summary>
    private static (PrologEngine Engine, DesktopWasmWorld World) Host(bool installer)
    {
        var engine = new PrologEngine();
        engine.ConsultString(Filler);
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = int.MaxValue,
            BundleInstaller = installer
                ? (eng, bytes) => WasmBundleTier.Install(eng, world, bytes)
                : null,
        };
        return (engine, world);
    }

    private static string Answers(PrologEngine engine)
    {
        WasmTierDelegate.ResetDiag();
        var r = engine.Query(Probe);
        Assert.True(r.Success);
        return r.Bindings["L"].ToString()!;
    }

    private static (long Entries, long Deopts, long Builtins, long Hops) Counters()
        => (WasmTierDelegate.DiagEntries, WasmTierDelegate.DiagDeopts,
            WasmTierDelegate.DiagBuiltins, WasmTierDelegate.DiagInWasmHops);

    [DiagFact]
    public void LinkedWithWasm_InstallsAtFirstLink_SameAnswersAndCountersAsLive()
    {
        var link = Link(Bake);
        Assert.True(link.Success, string.Join(", ", link.Diagnostics.Select(d => d.Message)));
        Assert.Contains(link.Diagnostics, d => d.Code == "wasm_module");
        var bundle = BundleReader.FromBytes(link.Bytes!);
        Assert.Single(bundle.WasmModules);

        // Live: the same bundle's statics compiled in-process, one group.
        var (live, liveWorld) = Host(installer: false);
        live.LoadBundle(bundle);
        live.IlPromotion.PendingWasmModules.Clear();
        live.Query("true.");
        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(live))
        {
            var (name, _) = RelocatingCompileEnv.NameOf(pred.FunctorId);
            if (name == "main" || (name.StartsWith("user$") && !name.StartsWith("user$filler")))
                members.Add(new WasmGroupMember(pred, addr,
                    live.IlPromotion.FloatPoolProvider?.Invoke(pred.FunctorId)));
        }
        Assert.Equal(10, members.Count);
        TieredEngine.Install(liveWorld, members, new EngineWasmCompileEnv(), live.IlPromotion);
        foreach (var m in members)
        {
            live.IlPromotion.RegisterBoundDelegate(m.Predicate.FunctorId,
                new WasmTierDelegate(m.Predicate.FunctorId, liveWorld).Invoke);
            live.IlPromotion.Wasm!.NoteInstalled(m.Predicate.FunctorId, m.Bias, m.Predicate);
        }
        string liveAnswers = Answers(live);
        var liveCounters = Counters();
        Assert.True(liveCounters.Entries > 0);

        // From the bundle: nothing compiled here; the query setup installs
        // the module when it links.
        var (host, _) = Host(installer: true);
        host.LoadBundle(bundle);
        var wasm = host.IlPromotion.Wasm!;
        Assert.Single(host.IlPromotion.PendingWasmModules);
        Assert.Empty(wasm.BundleFids);
        string bundleAnswers = Answers(host);
        Assert.Empty(host.IlPromotion.PendingWasmModules);
        Assert.Equal(members.Count, wasm.BundleFids.Count);
        Assert.Contains("installed from the bundle", wasm.BundleInstallNote);
        Assert.Equal(liveAnswers, bundleAnswers);
        Assert.StartsWith(".(-(red, circle(1))", bundleAnswers);
        Assert.Equal(liveCounters, Counters());
    }

    [Fact]
    public void NoInstaller_ModuleStaysPending_AnswersFromBytecode()
    {
        var bundle = BundleReader.FromBytes(Link(Bake).Bytes!);
        var (host, _) = Host(installer: false);
        host.LoadBundle(bundle);
        string answers = Answers(host);
        Assert.StartsWith(".(-(red, circle(1))", answers);
        Assert.Equal(0, WasmTierDelegate.DiagEntries);
        Assert.Single(host.IlPromotion.PendingWasmModules);
        Assert.Equal("no bundle modules", host.IlPromotion.Wasm!.BundleInstallNote);
    }

    [Fact]
    public void NoBaker_BundleCarriesNoModule()
    {
        var bundle = BundleReader.FromBytes(Link(baker: null).Bytes!);
        Assert.Empty(bundle.WasmModules);
    }

    [Fact]
    public void StripWamWithBaker_IsALinkError()
    {
        var link = Link(Bake, stripWam: true);
        Assert.False(link.Success);
        Assert.Contains(link.Diagnostics,
            d => d.Severity == LinkSeverity.Error && d.Code == "wasm_needs_wam");
    }

    [Fact]
    public void ModuleBakedFromOtherCode_IsRefused()
    {
        // The module of the corpus, spliced into a bundle whose color/1 has
        // another clause: its cursor offsets were taken against other code.
        var module = BundleReader.FromBytes(Link(Bake).Bytes!).WasmModules[0];
        var other = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(
                Corpus.Replace("color(blue).", "color(blue). color(black)."), "user") },
            EntryPoints = new[] { new PredicateRef("main", 0) },
        });
        Assert.True(other.Success);
        var bundle = BundleReader.FromBytes(other.Bytes!).WithWasmModules(new[] { module });
        var (host, _) = Host(installer: true);
        host.LoadBundle(bundle);
        string answers = Answers(host);
        Assert.Contains("-(black, ", answers);
        var wasm = host.IlPromotion.Wasm!;
        Assert.Empty(wasm.BundleFids);
        Assert.Equal(0, WasmTierDelegate.DiagEntries);
        Assert.Contains("user$color/1 has other code", wasm.BundleInstallNote);
    }
}
