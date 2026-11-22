using System.Reflection;
using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A bundle a running goal loads is linked into that goal's code,
/// and its wasm module is installed there, against those addresses. The
/// module waited for the setup of the next query: a notebook's session is
/// one query, so a library a cell imported never ran from its module, and
/// its predicates were compiled one at a time as each ran enough.</summary>
public sealed class LiveBundleInstallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shumway-livebundle-{Guid.NewGuid():N}");

    public LiveBundleInstallTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string LibrarySource(string name)
    {
        using var rs = Assembly.Load(new AssemblyName("Shumway.Libraries"))
            .GetManifestResourceStream(name + ".pl");
        Assert.NotNull(rs);
        using var reader = new StreamReader(rs);
        return reader.ReadToEnd();
    }

    private string BakedLibrary(string name) => BakedLibrary(_dir, name);

    /// <summary>The library as the build bakes it for the web, in a file of
    /// <paramref name="directory"/>; its path, as a goal names one.</summary>
    internal static string BakedLibrary(string directory, string name)
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(LibrarySource(name), name) },
            Library = true,
            WasmBaker = b => WasmBundleTier.Bake(b, stdlib: false),
        });
        Assert.True(link.Success, string.Join(", ", link.Diagnostics.Select(d => d.Message)));
        // The statics, and the snapshot of the library's attribute_goals/4.
        Assert.Equal(2, BundleReader.FromBytes(link.Bytes!).WasmModules.Count);
        string path = Path.Combine(directory, name + ".shum");
        File.WriteAllBytes(path, link.Bytes!);
        return path.Replace(Path.DirectorySeparatorChar, '/');
    }

    // No promotion of its own: the bundle's module is the only wasm it gets.
    private static PrologEngine Host(bool live)
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = int.MaxValue,
            // Without the addresses of the goal's own link there is only the
            // static link to install against, which is what there was.
            BundleInstaller = (eng, bytes, linked) => linked is not null && !live
                ? (Array.Empty<int>(), "not installed: no live install")
                : WasmBundleTier.Install(eng, world, bytes, linked),
            StaleEvicted = world.Evict,
            ShadowRetired = world.Retire,
            LiveRefreshed = world.RefreshLiveAddresses,
        };
        return engine;
    }

    private const string Goal = "coroutining:freeze(X, Y = done), coroutining:dif(A, B), A = 1, X = go";

    [Fact]
    public void TheModuleOfABundleAGoalLoads_IsInstalledForTheRestOfThatGoal()
    {
        string path = BakedLibrary("coroutining");
        var engine = Host(live: true);

        var loading = engine.Query($"consult('{path}'), {Goal}.");
        var wasm = engine.IlPromotion.Wasm!;
        int installed = wasm.BundleFids.Count;
        int queued = engine.IlPromotion.PendingWasmModules.Count;
        // The next query links the library again, somewhere else.
        var next = engine.Query($"{Goal}.");

        Assert.True(loading.Success);
        Assert.Equal("done", loading.Bindings["Y"].ToString());
        Assert.Equal(0, queued);
        Assert.True(installed > 50, $"{installed} installed: {wasm.BundleInstallNote}");
        Assert.True(next.Success);
        Assert.Equal("done", next.Bindings["Y"].ToString());
        Assert.True(wasm.BundleFids.Count > 50, $"{wasm.BundleFids.Count} after the next query");
    }

    [DiagFact]
    public void TheGoalThatLoadedTheBundle_RunsItsPredicatesFromTheModule()
    {
        string path = BakedLibrary("coroutining");
        var engine = Host(live: true);
        WasmTierDelegate.ResetDiag();

        var loading = engine.Query($"consult('{path}'), {Goal}.");

        Assert.True(loading.Success);
        Assert.True(WasmTierDelegate.DiagEntries > 0, "the library's predicates did not run as wasm");
    }

    /// <summary>Counter-proof, and what there was: the module stays queued
    /// until a query is set up.</summary>
    [Fact]
    public void WithoutTheLiveInstall_TheModuleWaitsForTheNextQuery()
    {
        string path = BakedLibrary("coroutining");
        var engine = Host(live: false);

        var loading = engine.Query($"consult('{path}'), {Goal}.");
        var wasm = engine.IlPromotion.Wasm!;
        int installed = wasm.BundleFids.Count;
        int queued = engine.IlPromotion.PendingWasmModules.Count;
        var next = engine.Query($"{Goal}.");

        Assert.True(loading.Success);
        Assert.Equal(0, installed);
        Assert.Equal(2, queued);
        Assert.True(next.Success);
        Assert.True(wasm.BundleFids.Count > 50, $"{wasm.BundleFids.Count} after the next query");
    }
}
