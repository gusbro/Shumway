using System.Reflection;
using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>An engine library (coroutining here) as the build bakes it for
/// the web: a library link, with a wasm module, loaded through
/// use_module into an engine with an installer. Its predicates must run
/// from the baked module, not from a compile in the loading engine.
/// Coroutining is the library with a dynamic clause (its attribute_goals/4
/// hook, declared dynamic by the prelude) whose body has a disjunction:
/// the helper minted for it at query setup carries a per-process number,
/// and a bake that took it as a member was refused whole by every other
/// process ("member $disj_N/5 is not linked").</summary>
public class LibraryBundleWasmTests
{
    private static string LibrarySource(string name)
    {
        // The source the build embeds next to the bundle it bakes.
        using var rs = Assembly.Load(new AssemblyName("Shumway.Libraries"))
            .GetManifestResourceStream(name + ".pl");
        Assert.NotNull(rs);
        using var reader = new StreamReader(rs);
        return reader.ReadToEnd();
    }

    private static Bundle BakeLibrary(string name, bool wasm)
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(LibrarySource(name), name) },
            Library = true,
            WasmBaker = wasm ? b => WasmBundleTier.Bake(b, stdlib: false) : null,
        });
        Assert.True(link.Success, string.Join(", ", link.Diagnostics.Select(d => d.Message)));
        return BundleReader.FromBytes(link.Bytes!);
    }

    private static PrologEngine Host(DesktopWasmWorld world)
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = int.MaxValue,
            BundleInstaller = (eng, bytes) => WasmBundleTier.Install(eng, world, bytes),
        };
        return engine;
    }

    [Fact]
    public void LibraryLinkedWithWasm_RunsFromTheBakedModule()
    {
        var bundle = BakeLibrary("coroutining", wasm: true);
        Assert.Single(bundle.WasmModules);
        var engine = Host(new DesktopWasmWorld());
        engine.LoadBundle(bundle);
        Assert.Single(engine.IlPromotion.PendingWasmModules);

        WasmTierDelegate.ResetDiag();
        var r = engine.Query("freeze(X, Y = done), dif(A, B), A = 1, X = go.");
        Assert.True(r.Success);
        Assert.Equal("done", r.Bindings["Y"].ToString());
        var wasm = engine.IlPromotion.Wasm!;
        Assert.Empty(engine.IlPromotion.PendingWasmModules);
        Assert.Contains("installed from the bundle", wasm.BundleInstallNote);
        Assert.True(wasm.BundleFids.Count > 50, $"{wasm.BundleFids.Count} installed: {wasm.BundleInstallNote}");
        Assert.True(WasmTierDelegate.DiagEntries > 0, "the library's predicates did not run as wasm");
    }

    [Fact]
    public void LibraryLinkedWithoutWasm_HasNoModule()
    {
        var bundle = BakeLibrary("coroutining", wasm: false);
        Assert.Empty(bundle.WasmModules);
    }
}
