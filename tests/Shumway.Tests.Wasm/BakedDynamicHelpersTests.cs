using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>The rules of a bundle's dynamic predicates take helper predicates
/// when an engine first links them (<c>'$disj_N'</c>, <c>'$neg_N'</c>), numbered
/// by that engine's counter. The bake compiles them into a module of their
/// own, as the IL tier's bake of a dynamic predicate takes the helpers it
/// reaches, under names every process gives them; an engine that numbered
/// them differently installs that module on its own helpers of the same
/// rules. They were left out, and a host compiled them at every load.</summary>
public class BakedDynamicHelpersTests
{
    private const string Library = """
        :- module(routes, [route/2]).
        :- dynamic(hop/2).
        hop(X, Y) :- ( X == a -> Y = b ; X == b -> Y = c ; Y = end ).
        hop(X, Y) :- \+ X == a, Y = far(X).
        route(X, Y) :- hop(X, Y).
        """;

    // The same statics, and a rule of hop/2 with another body.
    private static readonly string OtherRules = Library.Replace("Y = end", "Y = stop");

    // Rules consulted before the bundle in the loading engine: its counter
    // has handed out the numbers the baking engine gave the bundle's helpers.
    private const string Filler = """
        shift(X) :- ( X = 1 ; X = 2 ), \+ X = 3.
        shift(X) :- ( X = 4 -> true ; X = 5 ).
        shift(X) :- \+ X = 6, ( X = 7 ; X = 8 ; X = 9 ).
        """;

    private static string Goal(string last)
        => "findall(X-Y, (member(X, [a, b, q]), routes:route(X, Y)), L), "
         + $"L == [a-b, b-c, b-far(b), q-{last}, q-far(q)].";

    private static Bundle Linked(string source)
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(source, "routes") },
            Library = true,
            WasmBaker = b => WasmBundleTier.Bake(b, stdlib: false),
        });
        Assert.True(link.Success, string.Join(", ", link.Diagnostics.Select(d => d.Message)));
        return BundleReader.FromBytes(link.Bytes!);
    }

    // No promotion of its own: the bundle's modules are the only wasm it gets.
    private static PrologEngine Host(bool shifted)
    {
        var engine = new PrologEngine();
        if (shifted)
        {
            engine.ConsultString(Filler);
            Assert.True(engine.Query("shift(5).").Success);
        }
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = int.MaxValue,
            BundleInstaller = (eng, bytes, linked) => WasmBundleTier.Install(eng, world, bytes, linked),
            StaleEvicted = world.Evict,
        };
        return engine;
    }

    private static bool Answers(PrologEngine engine, string last = "end")
        => engine.Query(Goal(last)).Success;

    private static WasmRelocatableModule HelpersOf(Bundle bundle)
    {
        // The statics, the helpers, and the snapshot of hop/2.
        Assert.Equal(3, bundle.WasmModules.Count);
        Assert.False(Read(bundle.WasmModules[0]).CarriesPortableNames);
        var helpers = Read(bundle.WasmModules[1]);
        Assert.True(helpers.CarriesPortableNames);
        return helpers;
    }

    private static WasmRelocatableModule Read(byte[] bytes)
        => WasmRelocatableModule.Read(new MemoryStream(bytes));

    [Fact]
    public void TheHelpersOfABundlesDynamicRules_AreBakedInAModuleOfTheirOwn()
    {
        var helpers = HelpersOf(Linked(Library));

        // Two for the if-then-else chain's branches and one for the negation, at least.
        Assert.True(helpers.Members.Count >= 3, $"{helpers.Members.Count} members");
        Assert.All(helpers.Members, m => Assert.True(WasmPortableNames.IsPortable(m.Name), m.Name));
        Assert.Contains(helpers.Members, m => m.Name.Contains("$neg_@"));
        // No name of the module holds a number of the baking engine's counter.
        Assert.All(helpers.Relocations, r => Assert.DoesNotMatch(@"\$[a-z]+_\d+$", r.Name));
        Assert.All(helpers.CallSites, s => Assert.DoesNotMatch(@"\$[a-z]+_\d+$", s.CalleeName));
    }

    [Fact]
    public void AnEngineThatNumberedThemDifferently_InstallsThemOnItsOwnHelpers()
    {
        var bundle = Linked(Library);
        var helpers = HelpersOf(bundle);

        // What the baking engine called them: a fresh engine's names.
        var baker = new PrologEngine();
        baker.LoadBundle(bundle);
        baker.Query("true.");
        var bakerNames = Inverse(baker.PortableHelperNames());

        var host = Host(shifted: true);
        host.LoadBundle(bundle);
        bool answers = Answers(host);
        var hostNames = Inverse(host.PortableHelperNames());
        var wasm = host.IlPromotion.Wasm!;

        Assert.True(answers);
        Assert.Empty(host.IlPromotion.PendingWasmModules);
        Assert.Contains("installed from the bundle", wasm.BundleInstallNote);
        var installed = wasm.BundleFids.Select(f => RelocatingCompileEnv.NameOf(f).Name).ToList();
        foreach (var member in helpers.Members)
        {
            string portable = member.Name.Substring(member.Name.LastIndexOf('$'));
            string prefix = member.Name.Substring(0, member.Name.LastIndexOf('$'));
            // The guard: the two engines numbered this helper differently.
            Assert.NotEqual(bakerNames[portable], hostNames[portable]);
            Assert.Contains(prefix + hostNames[portable], installed);
            Assert.DoesNotContain(prefix + bakerNames[portable], installed);
        }
    }

    [DiagFact]
    public void TheHelpers_RunFromTheModule()
    {
        var bundle = Linked(Library);
        HelpersOf(bundle);
        // The helpers' module alone: an entry into wasm is a helper's.
        var host = Host(shifted: true);
        host.LoadBundle(bundle.WithWasmModules(new[] { bundle.WasmModules[1] }));
        WasmTierDelegate.ResetDiag();

        bool answers = Answers(host);

        Assert.True(answers);
        Assert.True(WasmTierDelegate.DiagEntries > 0, "no helper ran as wasm");
    }

    /// <summary>Counter-proof: with the statics' module alone no helper is
    /// installed, as before; the answers are the same.</summary>
    [Fact]
    public void WithoutTheirModule_TheHelpersStayOnBytecode()
    {
        var bundle = Linked(Library);
        HelpersOf(bundle);
        var host = Host(shifted: true);
        host.LoadBundle(bundle.WithWasmModules(new[] { bundle.WasmModules[0] }));

        bool answers = Answers(host);

        Assert.True(answers);
        var installed = host.IlPromotion.Wasm!.BundleFids.Select(f => RelocatingCompileEnv.NameOf(f).Name);
        Assert.DoesNotContain(installed, n => host.PortableHelperNames().ContainsKey(n.Substring(n.LastIndexOf('$'))));
    }

    [Fact]
    public void AnEngineWithoutOneOfTheRules_RefusesTheHelpersModule_AndKeepsTheStatics()
    {
        var bundle = Linked(Library);
        HelpersOf(bundle);
        // The other bundle's code with this one's modules: route/2 is the
        // same code, and the first rule of hop/2 is another.
        var other = Linked(OtherRules).WithWasmModules(bundle.WasmModules);
        var host = Host(shifted: false);
        host.LoadBundle(other);

        bool answers = Answers(host, last: "stop");
        var wasm = host.IlPromotion.Wasm!;

        Assert.True(answers);
        Assert.Contains("has no counterpart in this engine", wasm.BundleInstallNote);
        Assert.Contains(wasm.BundleFids, f => RelocatingCompileEnv.NameOf(f).Name.EndsWith("route"));
        Assert.Empty(host.IlPromotion.PendingWasmModules);
    }

    private static Dictionary<string, string> Inverse(Dictionary<string, string> names)
    {
        var inverse = new Dictionary<string, string>();
        foreach (var (live, portable) in names) inverse[portable] = live;
        return inverse;
    }
}
