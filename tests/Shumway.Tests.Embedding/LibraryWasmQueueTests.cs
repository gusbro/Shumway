using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-075, decision 12: the baked wasm modules of an attached library are
/// queued at the attach, each to install with the members that are linked,
/// and queued again when a module of the library comes in while one still
/// holds members that are not. With no wasm tier nothing drains the queue,
/// so the test reads it.
/// </summary>
public sealed class LibraryWasmQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_library_wasm_queue_" + Guid.NewGuid().ToString("N"));

    public LibraryWasmQueueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Opaque stand-ins: only their bytes tell them apart.
    private static readonly byte[] Statics = { 1 }, Helpers = { 2 };

    [Fact]
    public void TheModules_AreQueuedAtTheAttach_AndAgainAsAModuleComesIn()
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource("pa(1).\nca(X) :- pa(X).\n", "ma", ShmoBuildMode.Release),
                ShmoCompiler.CompileSource("pb(2).\ncb(X) :- pb(X).\n", "mb", ShmoBuildMode.Release),
            },
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            WasmBaker = _ => new[] { Statics, Helpers },
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        File.WriteAllBytes(Path.Combine(_dir, "queued.shum"), link.Bytes!);
        var e = new PrologEngine { Out = new StringWriter(), Warnings = new StringWriter() };
        e.AddLibraryDirectory(_dir);
        var store = e.IlPromotion;
        byte[][] Pending() => store.PendingWasmModules.Select(m => m.ToArray()).ToArray();

        Assert.True(e.Query("ensure_loaded(bundle(queued)).").Success);
        Assert.Equal(new[] { Statics, Helpers }, Pending());
        Assert.Equal(2, store.PartialWasmModules.Count);

        // As a tier would leave them: the helpers' module complete, the
        // statics' module installed without mb.
        store.PendingWasmModules.Clear();
        store.PartialWasmModules.RemoveWhere(m => m[0] == Helpers[0]);
        Assert.True(e.Query("ca(1).").Success);
        Assert.Equal(new[] { Statics }, Pending());

        store.PendingWasmModules.Clear();
        Assert.True(e.Query("ca(1).").Success);
        Assert.Empty(Pending());
        Assert.True(e.Query("cb(2).").Success);
        Assert.Equal(new[] { Statics }, Pending());
    }
}
