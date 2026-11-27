using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>
/// A module's <c>initialization/1</c> goals run where its bundle loads. The
/// wasm bake loads the bundle into an engine to compile its code, and runs
/// none of them.
/// </summary>
public sealed class BakeRunsNoInitializationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway-bake-init-" + Guid.NewGuid().ToString("N"));

    public BakeRunsNoInitializationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static LinkResult Link(string source) => ShmoLinker.Link(new LinkConfig
    {
        Objects = new[] { ShmoCompiler.CompileSource(source, "starts", ShmoBuildMode.Release) },
        EntryPoints = Array.Empty<PredicateRef>(),
        Library = true,
        WasmBaker = b => WasmBundleTier.Bake(b, stdlib: false),
    });

    [Fact]
    public void TheInitialization_RunsWhenTheBundleLoads_NotWhenItIsBaked()
    {
        string log = Path.Combine(_dir, "ran.txt").Replace('\\', '/');
        var link = Link($@"
count([], N, N).
count([_|T], N0, N) :- N1 is N0 + 1, count(T, N1, N).
:- initialization((count([a, b, c], 0, N), open('{log}', append, S), write(S, N), nl(S), close(S))).
");
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        Assert.NotEmpty(link.Bundle!.WasmModules);
        Assert.False(File.Exists(log), "the bake ran the initialization goal");

        var engine = new PrologEngine { Out = new StringWriter(), Warnings = new StringWriter() };
        engine.LoadBundle(BundleReader.FromBytes(link.Bytes!));
        Assert.Equal(new[] { "3" }, File.ReadAllLines(log));
    }

    [Fact]
    public void AnInitializationThatHalts_DoesNotEndTheBake()
    {
        var link = Link(@"
count([], N, N).
count([_|T], N0, N) :- N1 is N0 + 1, count(T, N1, N).
:- initialization((count([a], 0, _), halt)).
");
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        Assert.NotEmpty(link.Bundle!.WasmModules);
    }
}
