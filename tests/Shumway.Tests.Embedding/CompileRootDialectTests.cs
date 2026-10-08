using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A library compiled through the consult as a root (what WebShumway
/// does for each library of an imported collection) is read in its collection's
/// dialect, as use_module would load it: Scryer's builtins.pl declares
/// predicates with `:- non_counted_backtracking P/N`, an operator only Scryer's
/// VM has, and failed with a syntax error when read plainly.</summary>
public sealed class CompileRootDialectTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"shumway-rootdialect-{Guid.NewGuid():N}");

    public CompileRootDialectTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string ScryerRoot = """
        :- module(sroot, [hello/1]).
        :- non_counted_backtracking hello/1.
        hello(X) :- X = "hi".
        """;

    private (string Module, ShmoObject Object) Compile(string? dialect, IReadOnlyList<string> dirs)
    {
        string root = Path.Combine(_dir, "sroot.pl");
        File.WriteAllText(root, ScryerRoot);
        var errors = new List<ShmoCompileError>();
        var compiled = ShmoViaConsult.CompileMany(
            new[] { root }, dirs, ShmoBuildMode.Release, errors, dialect, new StringWriter());
        Assert.Empty(errors);
        var (module, obj, _, _) = Assert.Single(compiled, c => c.ModuleName == "sroot");
        return (module, obj);
    }

    [Fact]
    public void AnExplicitDialectReadsTheRootInIt()
    {
        var (_, obj) = Compile("scryer", new[] { _dir });
        Assert.Equal("scryer", obj.Dialect);
    }

    [Fact]
    public void ATaggedDirectoryGivesItsRootsItsDialect()
    {
        var (_, obj) = Compile(null, new[] { "scryer:" + _dir });
        Assert.Equal("scryer", obj.Dialect);
    }

    [Fact]
    public void TheCompiledRootLoadsFromItsBundle()
    {
        string root = Path.Combine(_dir, "sroot.pl");
        File.WriteAllText(root, ScryerRoot);
        var errors = new List<ShmoCompileError>();
        var compiled = ShmoViaConsult.CompileMany(
            new[] { root }, new[] { _dir }, ShmoBuildMode.Release, errors, "scryer", new StringWriter());
        Assert.Empty(errors);
        File.WriteAllBytes(Path.Combine(_dir, "sroot.shum"), Librarian.CreateArchive(compiled
            .Select(c => new BundleArchiveMember(c.ModuleName + ".shmo", ShmoWriter.ToBytes(c.Object)))
            .ToList(), null));
        File.Delete(root);
        var e = new PrologEngine();
        e.AddLibraryDirectory(_dir, "scryer");
        e.ConsultString(":- use_module(library(sroot)).");
        Assert.True(e.Query("hello(X), X == [h, i].").Success);
    }
}
