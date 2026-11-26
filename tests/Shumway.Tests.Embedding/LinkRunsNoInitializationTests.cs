using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A module's <c>initialization/1</c> goals run where its bundle loads. The
/// link that persists the bundle's IL loads it into an engine to compile its
/// code, and runs none of them.
/// </summary>
public sealed class LinkRunsNoInitializationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_link_init_" + Guid.NewGuid().ToString("N"));

    public LinkRunsNoInitializationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static LinkResult Link(string source) => ShmoLinker.Link(new LinkConfig
    {
        Objects = new[] { ShmoCompiler.CompileSource(source, "starts", ShmoBuildMode.Release) },
        EntryPoints = Array.Empty<PredicateRef>(),
        Library = true,
        IncludeCompiledIl = true,
    });

    [Fact]
    public void TheInitialization_RunsWhenTheBundleLoads_NotWhenItIsLinked()
    {
        string log = Path.Combine(_dir, "ran.txt").Replace('\\', '/');
        var link = Link($@"
greet(X) :- X = hello.
:- initialization((greet(X), open('{log}', append, S), write(S, X), nl(S), close(S))).
");
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(link.Bundle!.Entries.Single(e => e.ModuleName == "starts").CompiledIl);
        Assert.False(File.Exists(log), "the link ran the initialization goal");

        var engine = new PrologEngine { Out = new StringWriter(), Warnings = new StringWriter() };
        engine.LoadBundle(BundleReader.FromBytes(link.Bytes!));
        Assert.Equal(new[] { "hello" }, File.ReadAllLines(log));
    }

    [Fact]
    public void AnInitializationThatHalts_DoesNotEndTheLink()
    {
        var link = Link(@"
greet :- write(hello), nl.
:- initialization((greet, halt)).
");
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(link.Bytes);
    }
}
