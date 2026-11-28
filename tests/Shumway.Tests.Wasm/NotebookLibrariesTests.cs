using System.Text.Json;
using System.Text.Json.Nodes;
using Shumway.Embedding;
using Shumway.Jupyter;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-070, O3: a notebook session finds the library collections of
/// its page, and <c>jupyter:import_library/1</c> asks the page for another.</summary>
public sealed class NotebookLibrariesTests : IDisposable
{
    private readonly NotebookHarness _notebooks = new();
    private readonly string _collections = Path.Combine(
        Path.GetTempPath(), "shumway-collections-" + Guid.NewGuid().ToString("N"));

    public NotebookLibrariesTests() => Directory.CreateDirectory(_collections);

    public void Dispose()
    {
        _notebooks.Dispose();
        if (Directory.Exists(_collections)) Directory.Delete(_collections, recursive: true);
    }

    /// <summary>A page whose collections are the directories of one folder,
    /// and which records what it was asked to import.</summary>
    private sealed class Page(string collections) : INotebookLibraries
    {
        public readonly List<(string Dialect, string Location)> Imports = new();
        public Func<string, string, string>? OnImport;

        public void Refresh(PrologEngine engine)
        {
            foreach (string dir in Directory.GetDirectories(collections)) engine.AddLibraryDirectory(dir);
        }

        public string Import(PrologEngine engine, string dialect, string location)
        {
            lock (Imports) Imports.Add((dialect, location));
            return OnImport?.Invoke(dialect, location) ?? $"% imported '{dialect}' '{location}'";
        }
    }

    private static string Call(int id, string code)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "call", @params = new { code } });

    private static JsonNode First(NotebookServer server, int id, string code)
    {
        string? line = NotebookHarness.Ask(server, Call(id, code));
        Assert.NotNull(line);
        return JsonNode.Parse(line)!["result"]!["1"]!;
    }

    private static string Message(JsonNode entry)
        => (string)entry["error"]!["data"]!["prolog_message"]!;

    [Fact]
    public void ACollectionThePageGetsWhileTheSessionRuns_IsFoundByTheNextCell()
    {
        var page = new Page(_collections);
        NotebookServer server = _notebooks.Open("a", libraries: page).Server;
        JsonNode before = First(server, 1, "catch(use_module(library(o3_greeting)), E, true).");
        Assert.Contains("existence_error", before.ToJsonString());

        // What another tab, or the demo, imports after this session started.
        string collection = Path.Combine(_collections, "greetings");
        Directory.CreateDirectory(collection);
        File.WriteAllText(Path.Combine(collection, "o3_greeting.pl"),
            ":- module(o3_greeting, [greet/1]).\ngreet(hello).\n");

        JsonNode after = First(server, 2, "use_module(library(o3_greeting)), greet(G).");

        Assert.Null(after["error"]);
        Assert.Equal("hello", (string)after["bindings"]!["G"]!);
    }

    /// <summary>A library compiled into a bundle at the root of a collection,
    /// as the page compiles one the first time it is used.</summary>
    private static void CompileInto(string collection, string library, string source)
    {
        string sources = Path.Combine(collection, "src");
        Directory.CreateDirectory(sources);
        string file = Path.Combine(sources, library + ".pl");
        File.WriteAllText(file, source);
        var errors = new List<ShmoCompileError>();
        var compiled = ShmoViaConsult.CompileMany(
            new[] { file }, new[] { collection, sources }, ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        byte[] bytes = Librarian.CreateArchive(compiled
            .Select(c => new BundleArchiveMember(c.ModuleName + ".shmo", ShmoWriter.ToBytes(c.Object)))
            .ToList(), null);
        File.WriteAllBytes(Path.Combine(collection, library + ".shum"), bytes);
    }

    [Fact]
    public void ACompiledLibrary_IsCalledInTheCellThatImportsIt()
    {
        var page = new Page(_collections);
        NotebookServer server = _notebooks.Open("a", libraries: page).Server;
        CompileInto(Path.Combine(_collections, "built"), "o3_built",
            ":- module(o3_built, [built/1]).\nbuilt(yes).\n");

        JsonNode entry = First(server, 1, "use_module(library(o3_built)), built(X).");

        Assert.Null(entry["error"]);
        Assert.Equal("yes", (string)entry["bindings"]!["X"]!);
    }

    [Fact]
    public void ACompiledLibrary_IsCalledInTheCellAfterTheOneThatImportsIt()
    {
        var page = new Page(_collections);
        NotebookServer server = _notebooks.Open("a", libraries: page).Server;
        CompileInto(Path.Combine(_collections, "built2"), "o3_built2",
            ":- module(o3_built2, [built2/1]).\nbuilt2(yes).\n");

        Assert.Null(First(server, 1, "use_module(library(o3_built2)).")["error"]);
        JsonNode entry = First(server, 2, "built2(X).");

        Assert.Null(entry["error"]);
        Assert.Equal("yes", (string)entry["bindings"]!["X"]!);
    }

    [Theory]
    [InlineData(@"jupyter:import_library(scryer).", "scryer", "")]
    [InlineData(@"jupyter:import_library(swi:'https://example.org/lib.zip').", "swi", "https://example.org/lib.zip")]
    [InlineData(@"jupyter:import_library(trealla:""https://example.org/t.tar.gz"").", "trealla", "https://example.org/t.tar.gz")]
    [InlineData(@"jupyter:import_library('https://example.org/one.pl').", "", "https://example.org/one.pl")]
    [InlineData(@"jupyter:import_library(""http://example.org/one.pl"").", "", "http://example.org/one.pl")]
    public void ImportLibrary_HandsThePageTheDialectAndTheLocation(string code, string dialect, string location)
    {
        var page = new Page(_collections);
        NotebookServer server = _notebooks.Open("a", libraries: page).Server;

        JsonNode entry = First(server, 1, code);

        Assert.Null(entry["error"]);
        Assert.Equal(new[] { (dialect, location) }, page.Imports);
        // A cell's output is given without its final line end.
        Assert.Equal($"% imported '{dialect}' '{location}'", (string)entry["output"]!);
    }

    [Fact]
    public void ImportLibrary_OfAFileOfTheSession_HandsThePageItsFullPath()
    {
        var page = new Page(_collections);
        NotebookServer server = _notebooks.Open("a", libraries: page).Server;
        string file = Path.Combine(_notebooks.DirectoryOf("a"), "mine.zip");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

        JsonNode entry = First(server, 1, "jupyter:import_library(swi:'mine.zip').");

        Assert.Null(entry["error"]);
        (string dialect, string location) = Assert.Single(page.Imports);
        Assert.Equal("swi", dialect);
        Assert.Equal(Path.GetFullPath(file), Path.GetFullPath(location), ignoreCase: OperatingSystem.IsWindows());
    }

    [Theory]
    [InlineData(@"jupyter:import_library(X).", "instantiation_error")]
    [InlineData(@"jupyter:import_library(gnu).", "domain_error(library_dialect, gnu)")]
    [InlineData(@"jupyter:import_library(gnu:'https://example.org/x.pl').", "domain_error(library_dialect, gnu)")]
    [InlineData(@"jupyter:import_library(3).", "type_error(text, 3)")]
    [InlineData(@"jupyter:import_library('nothere.zip').", "existence_error(source_sink, 'nothere.zip')")]
    public void AnImportLibraryTheServerCanRefuse_DoesNotReachThePage(string code, string error)
    {
        var page = new Page(_collections);
        NotebookServer server = _notebooks.Open("a", libraries: page).Server;

        JsonNode entry = First(server, 1, code);

        Assert.Contains(error, Message(entry));
        Assert.Contains("import_library/1", Message(entry));
        Assert.Empty(page.Imports);
    }

    [Fact]
    public void AnImportThePageCouldNotMake_IsASystemErrorThatSaysWhy()
    {
        var page = new Page(_collections)
        {
            OnImport = (_, _) => throw new InvalidOperationException("example.org did not answer"),
        };
        NotebookServer server = _notebooks.Open("a", libraries: page).Server;

        JsonNode entry = First(server, 1, "jupyter:import_library('https://example.org/x.pl').");

        Assert.Contains("system_error", Message(entry));
        Assert.Contains("example.org did not answer", Message(entry));
        Assert.EndsWith("in import_library/1", Message(entry));
    }

    [Fact]
    public void AServerWithNoPage_SaysItImportsNothing()
    {
        NotebookServer server = _notebooks.Open("a").Server;

        JsonNode entry = First(server, 1, "jupyter:import_library(scryer).");

        Assert.Contains("no page to import a library into", Message(entry));
    }
}
