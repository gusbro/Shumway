using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A program that imports one of the engine's own libraries links:
/// its calls into the library resolve against the library's public
/// predicates, the library's code stays in its own bundle, and the program's
/// bundle names it so loading the bundle loads the library first. The linker
/// used to report every such call as a missing predicate, and a bundle
/// linked anyway ran without the library.</summary>
public sealed class EngineLibraryLinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_englib_link_" + Guid.NewGuid().ToString("N"));

    public EngineLibraryLinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private LinkResult LinkConsulted(string source)
    {
        string path = Path.Combine(_dir, "prog.pl");
        File.WriteAllText(path, source);
        var errors = new List<ShmoCompileError>();
        var compiled = ShmoViaConsult.CompileMany(
            new[] { path }, Array.Empty<string>(), ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        return Link(compiled.Select(c => c.Object).ToArray());
    }

    private static LinkResult Link(ShmoObject[] objects)
    {
        var r = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects,
            EntryPoints = new[] { new PredicateRef("main", 1) },
        });
        Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        return r;
    }

    private static string RunMain(byte[] bundleBytes)
    {
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bundleBytes));
        var q = engine.Query("main(X).");
        Assert.True(q.Success);
        return q["X"]!.ToString()!;
    }

    [Theory]
    [InlineData(":- use_module(library(clpfd)).\nmain(X) :- X in 1..3, X #> 2, label([X]).",
        "clpfd", "3")]
    [InlineData(":- use_module(library(coroutining)).\nmain(X) :- freeze(V, X = woke(V)), V = b.",
        "coroutining", "woke(b)")]
    public void AConsultedProgramLinksAndLoadsTheLibrary(string source, string library, string answer)
    {
        var r = LinkConsulted(source);
        Assert.Contains(library, r.Bundle!.EngineLibraries);
        Assert.Contains(library, BundleReader.FromBytes(r.Bytes!).EngineLibraries);
        Assert.Equal(answer, RunMain(r.Bytes!));
    }

    [Fact]
    public void AnObjectCompiledFileAtATimeLinksToo()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- use_module(library(coroutining)).\nmain(X) :- freeze(V, X = woke(V)), V = b.", "fat");
        var r = Link(new[] { obj });
        Assert.Equal(new[] { "coroutining" }, r.Bundle!.EngineLibraries);
        Assert.Equal("woke(b)", RunMain(r.Bytes!));
    }
}
