using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A bake whose group one member poisons: the member is refused, the
/// rest are baked, and the refusal is found in the phases before emission
/// rather than by compiling every member whole, a module each.</summary>
public sealed class BakeRefusalTests : IDisposable
{
    // An integer power is an arithmetic form the tier does not compile.
    private const string Source = """
        :- module(rlib).
        :- public cube/2.
        :- public inc/2.
        :- public twice/2.
        cube(X, Y) :- Y is X ^ 3.
        inc(X, Y) :- Y is X + 1.
        twice(X, Y) :- inc(X, Z), inc(Z, Y).
        """;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway-bake-refusal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ThePoisonerIsSortedOutBeforeEmissionAndTheRestIsBaked()
    {
        var log = new List<string>();
        byte[] archive = Librarian.CreateArchive(
            new[] { new BundleArchiveMember("rlib.shmo", ShmoWriter.ToBytes(ShmoCompiler.CompileSource(Source, "rlib"))) },
            b => WasmBundleTier.Bake(b, stdlib: false, log.Add));

        Assert.Contains(log, l => l.StartsWith("wasm: skipping cube/2", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.StartsWith("wasm: skipping inc/2", StringComparison.Ordinal)
                                        || l.StartsWith("wasm: skipping twice/2", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("refused, sorted out in", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains("compiled alone", StringComparison.Ordinal));
        Assert.Contains("wasm: 2 of 3 predicates baked", log);

        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "rlib.shum"), archive);
        var engine = new PrologEngine();
        engine.AddLibraryDirectory(_dir);
        engine.ConsultString(":- use_module(library(rlib)).");
        Assert.True(engine.Query("cube(2, 8), inc(2, 3), twice(2, 4).").Success);
    }

    private static Bundle Linked()
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Source, "rlib") },
            Library = true,
            WasmBaker = b => WasmBundleTier.Bake(b, stdlib: false),
        });
        Assert.True(link.Success, string.Join(", ", link.Diagnostics.Select(d => d.Message)));
        return BundleReader.FromBytes(link.Bytes!);
    }

    // What a host's batch is offered after it loads the bundle and runs a goal.
    private static (List<string> Offered, WasmPromotionStore Wasm) OfferedAfterLoading(Bundle bundle)
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var offered = new List<string>();
        var wasm = store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = int.MaxValue,
            BundleInstaller = (eng, bytes, linked) => WasmBundleTier.Install(eng, world, bytes, linked),
            BatchPromoter = candidates =>
            {
                foreach (var (pred, _) in candidates)
                    offered.Add(RelocatingCompileEnv.NameOf(pred.FunctorId).Name);
                return 0;
            },
        };
        engine.LoadBundle(bundle);
        Assert.True(engine.Query("rlib:cube(2, 8), rlib:twice(2, 4).").Success);
        Assert.Contains(wasm.BundleFids, f => RelocatingCompileEnv.NameOf(f).Name.EndsWith("twice"));
        wasm.PromoteAllStatics(engine);
        // The guard: the batch was offered the rest of the program.
        Assert.NotEmpty(offered);
        return (offered, wasm);
    }

    /// <summary>The module carries what the bake refused, and the host that
    /// installs it marks those refused. Its batch offered each one again at
    /// every load, to be refused again.</summary>
    [Fact]
    public void WhatTheBakeRefused_IsNotOfferedToTheBatchOfAHostThatLoadsTheBundle()
    {
        var bundle = Linked();
        var module = WasmRelocatableModule.Read(new MemoryStream(Assert.Single(bundle.WasmModules)));
        var refused = Assert.Single(module.Refused);
        Assert.EndsWith("cube", refused.Name);

        var (offered, wasm) = OfferedAfterLoading(bundle);

        Assert.DoesNotContain(offered, name => name.EndsWith("cube"));
        int cube = Assert.Single(wasm.UnpromotableFunctorIds(),
            f => RelocatingCompileEnv.NameOf(f).Name.EndsWith("cube"));
        Assert.Equal(refused.Reason, wasm.RefusalReason(cube));
    }

    /// <summary>Counter-proof: the same module without the list.</summary>
    [Fact]
    public void WithoutTheList_TheBatchIsOfferedIt()
    {
        var bundle = Linked();
        var module = WasmRelocatableModule.Read(new MemoryStream(Assert.Single(bundle.WasmModules)));
        byte[] bare = module.WithRefused(Array.Empty<(string, int, string)>()).ToBytes();

        var (offered, _) = OfferedAfterLoading(bundle.WithWasmModules(new[] { bare }));

        Assert.Contains(offered, name => name.EndsWith("cube"));
    }
}
