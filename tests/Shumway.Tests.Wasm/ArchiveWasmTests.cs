using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A librarian archive with a wasm module: what WebShumway writes
/// when it compiles a library under the wasm tier. Resolved by
/// use_module(library(X)) from a search-path directory, the archive's
/// module must install from the archive at the next link and run as wasm,
/// not compile in the loading engine. Members stay verbatim: adding one
/// drops the module, which was baked against the old member set.</summary>
public class ArchiveWasmTests : IDisposable
{
    private const string Source = """
        :- module(ilist).
        :- public rev/2.
        :- public sum/2.
        rev(L, R) :- rev(L, [], R).
        rev([], A, A).
        rev([X|Xs], A, R) :- rev(Xs, [X|A], R).
        sum([], 0).
        sum([X|Xs], S) :- sum(Xs, S0), S is S0 + X.
        """;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway-archive-wasm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static BundleArchiveMember Member(string source, string name)
        => new(name + ".shmo", ShmoWriter.ToBytes(ShmoCompiler.CompileSource(source, name)));

    private static byte[] Archive(bool wasm, params BundleArchiveMember[] members)
        => Librarian.CreateArchive(members,
            wasm ? b => WasmBundleTier.Bake(b, stdlib: false) : null);

    private PrologEngine HostWithLibrary(byte[] archive)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "ilist.shum"), archive);
        var world = new DesktopWasmWorld();
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = int.MaxValue,
            BundleInstaller = (eng, bytes) => WasmBundleTier.Install(eng, world, bytes),
        };
        engine.AddLibraryDirectory(_dir);
        return engine;
    }

    [Fact]
    public void ArchiveWithWasm_InstallsWhenTheLibraryLoads()
    {
        byte[] archive = Archive(wasm: true, Member(Source, "ilist"));
        Assert.Single(BundleReader.FromBytes(archive).WasmModules);
        // The members are the plain objects: the module rides in the trailer.
        Assert.Single(Librarian.ReadArchive(archive));

        var engine = HostWithLibrary(archive);
        engine.ConsultString(":- use_module(library(ilist)).");
        Assert.Single(engine.IlPromotion.PendingWasmModules);

        WasmTierDelegate.ResetDiag();
        var r = engine.Query("numlist(1, 200, L), rev(L, R), sum(R, S).");
        Assert.True(r.Success);
        Assert.Equal("20100", r.Bindings["S"].ToString());
        var wasm = engine.IlPromotion.Wasm!;
        Assert.Empty(engine.IlPromotion.PendingWasmModules);
        Assert.Contains("installed from the bundle", wasm.BundleInstallNote);
        Assert.Equal(3, wasm.BundleFids.Count);   // rev/2, rev/3, sum/2
        Assert.True(WasmTierDelegate.DiagEntries > 0, "the archive's predicates did not run as wasm");
    }

    [Fact]
    public void ArchiveWithoutWasm_CompilesNothingAtLoad()
    {
        byte[] archive = Archive(wasm: false, Member(Source, "ilist"));
        Assert.Empty(BundleReader.FromBytes(archive).WasmModules);
        var engine = HostWithLibrary(archive);
        engine.ConsultString(":- use_module(library(ilist)).");
        Assert.Empty(engine.IlPromotion.PendingWasmModules);
        var r = engine.Query("rev([a, b], R), R == [b, a].");
        Assert.True(r.Success);
        Assert.Empty(engine.IlPromotion.Wasm!.BundleFids);
    }

    [Fact]
    public void AddingAMember_DropsTheModule()
    {
        byte[] archive = Archive(wasm: true, Member(Source, "ilist"));
        byte[] grown = Librarian.AddMembers(archive,
            new[] { Member(":- module(other). :- public one/1. one(1).", "other") });
        Assert.Equal(2, Librarian.ReadArchive(grown).Count);
        Assert.Empty(BundleReader.FromBytes(grown).WasmModules);
    }
}
