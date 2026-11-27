using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-075 on the wasm tier: code promoted to a wasm module makes the
/// first call into a module of an attached library, and the first call of a
/// dynamic predicate whose clauses the library holds.</summary>
public sealed class DynamicLibraryWasmTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway-dynlib-wasm-" + Guid.NewGuid().ToString("N"));

    public DynamicLibraryWasmTests()
    {
        Directory.CreateDirectory(_dir);
        var objects = new[]
        {
            ShmoCompiler.CompileSource(@"
                :- dynamic(tbl/2).
                tbl(a, 1).
                tbl(b, 2).
                ", "t1"),
            ShmoCompiler.CompileSource(@"
                :- dynamic(tbl/2).
                tbl(c, 3).
                other(1).
                ", "t2"),
        };
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects,
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            AllowUndefined = true,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        File.WriteAllBytes(Path.Combine(_dir, "tables.shum"),
            BundleWriter.ToBytes(link.Bundle!, includeCompiledBytecode: true));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // The no branch promotes probe/3 and calls into nothing of the library.
    private const string Prober = @"
        :- dynamic(tbl/2).
        tbl(z, 0).
        probe(no, x, x).
        probe(tbl, K, V) :- tbl(K, V).
        probe(code, K, V) :- other(K), V = K.
        warm :- probe(no, _, _), probe(no, _, _).
        ";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PromotedCode_InstallsTheClauses_AndBringsTheModuleIn(bool attachFirst)
    {
        var e = TieredEngine.BuildWithWorld("").Engine;
        e.AddLibraryDirectory(_dir);
        e.ConsultString(Prober);
        if (attachFirst) Assert.True(e.Query("ensure_loaded(bundle(tables)).").Success);
        Assert.True(e.Query("warm, warm.").Success);
        Assert.NotEmpty(e.IlPromotion.PromotedFunctorIds());
        if (!attachFirst) Assert.True(e.Query("ensure_loaded(bundle(tables)).").Success);
        Assert.False(e.PrecompiledModules.ContainsKey("t2"));

        Assert.True(e.Query("findall(K, probe(tbl, K, _), L), L == [z, a, b, c].").Success);
        Assert.True(e.Query("findall(K, probe(tbl, K, _), L), L == [z, a, b, c].").Success, "the next query");

        Assert.True(e.Query("probe(code, K, V), K == 1, V == 1.").Success);
        Assert.True(e.PrecompiledModules.ContainsKey("t2"));
        Assert.True(e.Query("probe(code, K, _), K == 1.").Success, "the next query");
    }
}
