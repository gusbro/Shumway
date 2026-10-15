using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-038 §4 on the wasm tier: an archive that leaves out a library
/// it imports bakes its own predicates only, and its calls into the library
/// link at install against the library's own module.</summary>
public class ArchiveDependencyWasmTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway-archive-deps-wasm-" + Guid.NewGuid().ToString("N"));

    private string Src => Path.Combine(_dir, "src");
    private string Built => Path.Combine(_dir, "built");

    public ArchiveDependencyWasmTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Built);
        File.WriteAllText(Path.Combine(Src, "base.pl"), """
            :- module(base, [total/2]).
            total([], 0).
            total([X|Xs], S) :- total(Xs, S0), S is S0 + X.
            """);
        File.WriteAllText(Path.Combine(Src, "top.pl"), """
            :- module(top, [twice/2]).
            :- use_module(library(base)).
            twice(L, S) :- total(L, S1), S is 2 * S1.
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Compiles src/<paramref name="library"/>.pl against the
    /// archives built so far and writes its own archive, baked.</summary>
    private byte[] Build(string library)
    {
        var errors = new List<ShmoCompileError>();
        var compiled = ShmoViaConsult.CompileMany(
            new[] { Path.Combine(Src, library + ".pl") }, new[] { Built },
            ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        var members = compiled
            .Select(c => new BundleArchiveMember(c.ModuleName + ".shmo", ShmoWriter.ToBytes(c.Object)))
            .ToList();
        byte[] archive = Librarian.CreateArchive(members,
            b => WasmBundleTier.Bake(b, stdlib: false, prepare: e => e.AddLibraryDirectory(Built)));
        File.WriteAllBytes(Path.Combine(Built, library + ".shum"), archive);
        return archive;
    }

    private static IReadOnlyList<string> BakedNames(byte[] archive)
    {
        byte[] module = Assert.Single(BundleReader.FromBytes(archive).WasmModules);
        return WasmRelocatableModule.Read(new MemoryStream(module)).Members
            .Select(m => m.Name).ToList();
    }

    [Fact]
    public void AnArchiveBakesItsOwnPredicatesOnly()
    {
        Build("base");
        byte[] top = Build("top");
        Assert.Equal(new[] { "top" }, Librarian.ReadArchive(top).Select(Librarian.ModuleNameOf));
        var baked = BakedNames(top);
        Assert.Contains("top$twice", baked);
        Assert.DoesNotContain(baked, n => n.StartsWith("base$", StringComparison.Ordinal));
    }

    [DiagFact]
    public void ACallIntoTheLibraryRunsAsWasm()
    {
        Build("base");
        Build("top");
        var world = new DesktopWasmWorld();
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = int.MaxValue,
            BundleInstaller = (eng, bytes) => WasmBundleTier.Install(eng, world, bytes),
        };
        engine.AddLibraryDirectory(Built);
        engine.ConsultString(":- use_module(library(top)).");
        Assert.Equal(2, engine.IlPromotion.PendingWasmModules.Count);

        WasmTierDelegate.ResetDiag();
        var r = engine.Query("numlist(1, 100, L), twice(L, S).");
        Assert.True(r.Success);
        Assert.Equal("10100", r.Bindings["S"].ToString());
        Assert.Empty(engine.IlPromotion.PendingWasmModules);
        Assert.True(WasmTierDelegate.DiagEntries > 0, "the archives' predicates did not run as wasm");
    }
}
