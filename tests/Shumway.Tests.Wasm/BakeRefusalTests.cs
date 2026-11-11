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
}
