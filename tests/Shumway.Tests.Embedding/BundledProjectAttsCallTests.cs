using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A library the engine serves from a dialect shim with no module
/// (library('$project_atts'), which Scryer's iso_ext.pl imports for
/// copy_term/3) gives an importer nothing to import, but the importer still
/// depends on it. Loaded from source, the importer's directive loads it; from
/// the importer's bundle, or linked into a program, the dependency is what
/// brings it in.</summary>
public sealed class BundledProjectAttsCallTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"shumway-projatts-{Guid.NewGuid():N}");

    public BundledProjectAttsCallTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Lib = """
        :- module(ctlib, [ct/3]).
        :- use_module(library('$project_atts')).
        ct(Term, Copy, Gs) :-
            findall(Term-Rs, '$project_atts':term_residual_goals(Term, Rs), [Copy-Gs]).
        """;

    private const string Goal = "put_attr(A, m, 1), ct(A, B, G), G == [put_attr(B, m, 1)]";

    [Fact]
    public void FromSource()
    {
        File.WriteAllText(Path.Combine(_dir, "ctlib.pl"), Lib);
        var e = new PrologEngine();
        e.AddLibraryDirectory(_dir, "scryer");
        e.ConsultString(":- use_module(library(ctlib)).");
        Assert.True(e.Query(Goal + ".").Success);
    }

    [Fact]
    public void FromItsBundle()
    {
        string root = Path.Combine(_dir, "ctlib.pl");
        File.WriteAllText(root, Lib);
        var errors = new List<ShmoCompileError>();
        var compiled = ShmoViaConsult.CompileMany(
            new[] { root }, new[] { _dir }, ShmoBuildMode.Release, errors, "scryer", new StringWriter());
        Assert.Empty(errors);
        var (_, obj, _, _) = Assert.Single(compiled);
        Assert.Contains(obj.LibraryDeps, d => d.LibName == "$project_atts" && !d.Baked);
        File.WriteAllBytes(Path.Combine(_dir, "ctlib.shum"), Librarian.CreateArchive(compiled
            .Select(c => new BundleArchiveMember(c.ModuleName + ".shmo", ShmoWriter.ToBytes(c.Object)))
            .ToList(), null));
        File.Delete(root);

        var e = new PrologEngine();
        e.AddLibraryDirectory(_dir, "scryer");
        Assert.False(e.Query("current_predicate(term_residual_goals/2).").Success,
            "a fresh engine already has the shim, so the bundle proves nothing");
        e.ConsultString(":- use_module(library(ctlib)).");
        Assert.True(e.Query(Goal + ".").Success);
    }

    [Fact]
    public void LinkedWithTheProgram()
    {
        const string App = """
            :- use_module(library(ctlib)).
            main(ok) :- put_attr(A, m, 1), ct(A, B, G), G == [put_attr(B, m, 1)].
            """;
        var r = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource(Lib, "ctlib"),
                ShmoCompiler.CompileSource(App, "app"),
            },
            EntryPoints = new[] { new PredicateRef("main", 1) },
        });
        Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(r.Bytes!));
        Assert.True(e.Query("main(ok).").Success);
    }
}
