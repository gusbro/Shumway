using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A dynamic predicate a bundle gives clauses to is baked as its
/// snapshot (ADR-054), a module of its own, as the IL tier bakes it
/// (ADR-023). A host holds the module and installs it at the predicate's
/// first call, over the shadow region it links then, when the predicate has
/// the clauses the snapshot was compiled from. The predicate ran on the
/// interpreter until it had been called enough, and was compiled then.</summary>
public class BakedDynamicSnapshotTests
{
    private const string Library = """
        :- module(routes, [route/2, learn/2]).
        :- dynamic(hop/2).
        hop(X, Y) :- ( X == a -> Y = b ; X == b -> Y = c ; Y = end ).
        hop(X, Y) :- \+ X == a, Y = far(X).
        route(X, Y) :- hop(X, Y).
        learn(X, Y) :- assertz(hop(X, Y)).
        """;

    private static readonly string OtherRules = Library.Replace("Y = end", "Y = stop");

    // Rules consulted before the bundle in the loading engine: its counter
    // has handed out the numbers the baking engine gave the bundle's helpers.
    private const string Filler = """
        shift(X) :- ( X = 1 ; X = 2 ), \+ X = 3.
        shift(X) :- ( X = 4 -> true ; X = 5 ).
        """;

    private static string Goal(string last = "end", string more = "")
        => "findall(X-Y, (member(X, [a, b, q]), routes:route(X, Y)), L), "
         + $"L == [a-b, b-c, b-far(b), q-{last}, q-far(q){more}].";

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

    // The statics, the helpers of hop/2's rules, the snapshot of hop/2.
    private static WasmRelocatableModule SnapshotOf(Bundle bundle)
    {
        Assert.Equal(3, bundle.WasmModules.Count);
        var snapshot = WasmRelocatableModule.Read(new MemoryStream(bundle.WasmModules[2]));
        Assert.True(snapshot.IsSnapshot);
        return snapshot;
    }

    private static int Hop(Bundle bundle)
    {
        var member = SnapshotOf(bundle).Members[0];
        return FunctorTable.Intern(AtomTable.Intern(member.Name, permanent: true).Id, member.Arity);
    }

    // No promotion of its own: the bundle's modules are the only wasm it gets.
    private static PrologEngine Host()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Filler);
        Assert.True(engine.Query("shift(5).").Success);
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = int.MaxValue,
            Promoter = (_, _) => throw new InvalidOperationException("nothing is compiled here"),
            BundleInstaller = (eng, bytes, linked) => WasmBundleTier.Install(eng, world, bytes, linked),
            StaleEvicted = world.Evict,
            ShadowRetired = world.Retire,
            LiveRefreshed = world.RefreshLiveAddresses,
        };
        return engine;
    }

    [Fact]
    public void ADynamicPredicateABundleGivesClauses_IsBakedAsItsSnapshot()
    {
        var snapshot = SnapshotOf(Linked(Library));

        var member = Assert.Single(snapshot.Members);
        Assert.EndsWith("hop", member.Name);
        Assert.Equal(2, member.Arity);
        // It calls the helpers of its rules by the names every process gives them.
        Assert.Contains(snapshot.Relocations, r => r.Kind == WasmRelocKind.CallTarget
            && WasmPortableNames.IsPortable(r.Name));
        Assert.All(snapshot.Relocations, r => Assert.DoesNotMatch(@"\$[a-z]+_\d+$", r.Name));
    }

    [Fact]
    public void TheSnapshot_IsInstalledAtThePredicatesFirstCall()
    {
        var bundle = Linked(Library);
        int hop = Hop(bundle);
        var host = Host();
        host.LoadBundle(bundle);
        var wasm = host.IlPromotion.Wasm!;

        bool answers = host.Query(Goal()).Success;

        Assert.True(answers);
        Assert.Empty(host.IlPromotion.PendingWasmModules);
        Assert.Contains("installed from the bundle", wasm.BundleInstallNote);
        Assert.Equal(1, wasm.BakedSnapshotInstalls);
        Assert.True(wasm.HasShadow(hop));
    }

    [DiagFact]
    public void TheSnapshot_RunsThePredicate()
    {
        var bundle = Linked(Library);
        SnapshotOf(bundle);
        // The snapshot's module alone: an entry into wasm is hop/2's.
        var host = Host();
        host.LoadBundle(bundle.WithWasmModules(new[] { bundle.WasmModules[2] }));
        WasmTierDelegate.ResetDiag();

        bool answers = host.Query(Goal()).Success;

        Assert.True(answers);
        Assert.True(WasmTierDelegate.DiagEntries > 0, "hop/2 did not run as wasm");
    }

    [Fact]
    public void AMutation_RetiresIt_AndTheBakeIsNotUsedAgain()
    {
        var bundle = Linked(Library);
        int hop = Hop(bundle);
        var host = Host();
        host.LoadBundle(bundle);
        var wasm = host.IlPromotion.Wasm!;
        Assert.True(host.Query(Goal()).Success);
        Assert.True(wasm.HasShadow(hop));

        bool learned = host.Query("routes:learn(q, new).").Success;
        bool answers = host.Query(Goal(more: ", q-new")).Success;

        Assert.True(learned);
        Assert.True(answers);
        Assert.False(wasm.HasShadow(hop));
        Assert.Equal(1, wasm.BakedSnapshotInstalls);
        Assert.Equal(0, wasm.BakedSnapshotsHeld);
    }

    [Fact]
    public void AfterAConsultRebuildsTheCode_ItIsInstalledAgain()
    {
        var bundle = Linked(Library);
        int hop = Hop(bundle);
        var host = Host();
        host.LoadBundle(bundle);
        var wasm = host.IlPromotion.Wasm!;
        Assert.True(host.Query(Goal()).Success);
        Assert.Equal(1, wasm.BakedSnapshotInstalls);

        host.ConsultString("later(1). later(2).");
        bool answers = host.Query(Goal()).Success;

        Assert.True(answers);
        Assert.Equal(2, wasm.BakedSnapshotInstalls);
        Assert.True(wasm.HasShadow(hop));
    }

    [Fact]
    public void AnEngineWhoseClausesAreOthers_DoesNotUseTheBake()
    {
        var bundle = Linked(Library);
        int hop = Hop(bundle);
        // The other bundle's clauses with this one's modules.
        var host = Host();
        host.LoadBundle(Linked(OtherRules).WithWasmModules(bundle.WasmModules));
        var wasm = host.IlPromotion.Wasm!;

        bool answers = host.Query(Goal(last: "stop")).Success;

        Assert.True(answers);
        Assert.Equal(0, wasm.BakedSnapshotInstalls);
        Assert.False(wasm.HasShadow(hop));
        Assert.Equal(0, wasm.BakedSnapshotsHeld);
    }

    [Fact]
    public void WithBundlesOff_TheSnapshotLeaves_AndComesBackWhenTheyAreOn()
    {
        var bundle = Linked(Library);
        int hop = Hop(bundle);
        var host = Host();
        host.LoadBundle(bundle);
        var wasm = host.IlPromotion.Wasm!;
        Assert.True(host.Query(Goal()).Success);

        wasm.SetBundles(false);
        bool off = host.Query(Goal()).Success;
        bool shadowWhileOff = wasm.HasShadow(hop);
        wasm.SetBundles(true);
        bool on = host.Query(Goal()).Success;

        Assert.True(off);
        Assert.False(shadowWhileOff);
        Assert.True(on);
        Assert.True(wasm.HasShadow(hop));
        Assert.Equal(2, wasm.BakedSnapshotInstalls);
    }
}
