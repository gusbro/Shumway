using System.Reflection;
using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>jit_compile(off) is off for a library loaded after it as well.
/// A bundle's baked wasm module was installed at the next goal whatever the
/// tier's setting, so the library ran in wasm while everything else ran on
/// the interpreter. Held while the tier is off, it installs once the tier is
/// on again, at the next goal.</summary>
public class TierOffHoldsBundleModulesTests
{
    private static string LibrarySource(string name)
    {
        using var rs = Assembly.Load(new AssemblyName("Shumway.Libraries"))
            .GetManifestResourceStream(name + ".pl");
        Assert.NotNull(rs);
        using var reader = new StreamReader(rs);
        return reader.ReadToEnd();
    }

    private static Bundle CoroutiningWithWasm()
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(LibrarySource("coroutining"), "coroutining") },
            Library = true,
            WasmBaker = b => WasmBundleTier.Bake(b, stdlib: false),
        });
        Assert.True(link.Success, string.Join(", ", link.Diagnostics.Select(d => d.Message)));
        var bundle = BundleReader.FromBytes(link.Bytes!);
        Assert.Single(bundle.WasmModules);
        return bundle;
    }

    private const string Goal = "freeze(X, Y = done), dif(A, B), A = 1, X = go, Y == done.";

    [Fact]
    public void ALibraryLoadedWhileTheTierIsOffWaitsForItToBeOn()
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var wasm = store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = 0,   // jit_compile(off)
            BundleInstaller = (eng, bytes) => WasmBundleTier.Install(eng, world, bytes),
        };
        engine.LoadBundle(CoroutiningWithWasm());

        Assert.True(engine.Query(Goal).Success);
        Assert.Empty(wasm.BundleFids);
        Assert.Contains("held: jit_compile(off)", wasm.BundleInstallNote);

        wasm.Threshold = int.MaxValue;   // on again
        Assert.True(engine.Query(Goal).Success);
        Assert.True(wasm.BundleFids.Count > 50,
            $"{wasm.BundleFids.Count} installed: {wasm.BundleInstallNote}");
    }

    [Fact]
    public void BundlesOnInstallsAtTheNextGoal()
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var wasm = store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = int.MaxValue,
            BundleInstaller = (eng, bytes) => WasmBundleTier.Install(eng, world, bytes),
        };
        wasm.SetBundles(on: false);
        engine.LoadBundle(CoroutiningWithWasm());
        Assert.True(engine.Query(Goal).Success);
        Assert.Empty(wasm.BundleFids);

        wasm.SetBundles(on: true);
        Assert.True(engine.Query(Goal).Success);
        Assert.True(wasm.BundleFids.Count > 50,
            $"{wasm.BundleFids.Count} installed: {wasm.BundleInstallNote}");
    }
}
