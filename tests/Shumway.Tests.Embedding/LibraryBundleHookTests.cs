using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A library whose purpose is to install term_expansion /
/// goal_expansion hooks (Scryer's atts, which turns `:- attribute` into
/// get_atts/put_atts) must work the same from its compiled bundle as from its
/// source: a program consulted after `use_module` is expanded by those hooks.
/// The bundle used to hold the hook clauses as a local of the library's module,
/// which expanded nothing.</summary>
public sealed class LibraryBundleHookTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"shumway-hooklib-{Guid.NewGuid():N}");

    public LibraryBundleHookTests() => Directory.CreateDirectory(Path.Combine(_dir, "src"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // The hook bodies call helpers local to their module: the clause must run
    // there, as the consult of the source runs it.
    private const string DeclLib = """
        :- module(decl, []).
        user:term_expansion((:- declare(N)), Clauses) :- decl_clauses(N, Clauses).
        decl_clauses(N, [declared(N, N)]).
        user:goal_expansion(double(X, Y), Y is X * Factor) :- decl_factor(Factor).
        decl_factor(2).
        """;

    private const string MarkLib = """
        :- module(mark, []).
        user:term_expansion(marked(X), mark_of(X, Tag)) :- mark_tag(Tag).
        mark_tag(seen).
        """;

    /// <summary>Compiles one library as WebShumway does (consulted in an
    /// ephemeral engine, packed by the librarian) and leaves only the bundle
    /// on the search path.</summary>
    private void Bundle(string name, string source)
    {
        string src = Path.Combine(_dir, "src", name + ".pl");
        File.WriteAllText(src, source);
        var errors = new List<ShmoCompileError>();
        var compiled = ShmoViaConsult.CompileMany(
            new[] { src }, new[] { Path.Combine(_dir, "src") }, ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        byte[] bytes = Librarian.CreateArchive(compiled
            .Select(c => new BundleArchiveMember(c.ModuleName + ".shmo", ShmoWriter.ToBytes(c.Object)))
            .ToList(), null);
        File.WriteAllBytes(Path.Combine(_dir, name + ".shum"), bytes);
    }

    private PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.AddLibraryDirectory(_dir);
        return e;
    }

    [Fact]
    public void AProgramConsultedAfterTheBundleIsExpandedByItsHooks()
    {
        Bundle("decl", DeclLib);
        var e = Engine();
        e.ConsultString("""
            :- use_module(library(decl)).
            :- declare(a).
            :- declare(b).
            p(Y) :- double(21, Y).
            """);
        Assert.True(e.Query("declared(a, a), declared(b, b).").Success);
        var r = e.Query("p(Y).");
        Assert.True(r.Success);
        Assert.Equal("42", r["Y"]!.ToString());
    }

    [Fact]
    public void TwoBundlesAndAConsultedHookAllExpand()
    {
        Bundle("decl", DeclLib);
        Bundle("mark", MarkLib);
        var e = Engine();
        e.ConsultString("""
            term_expansion(local(X), local_of(X)).
            local(first).
            """);
        Assert.True(e.Query("local_of(first).").Success);
        e.ConsultString("""
            :- use_module(library(decl)).
            :- use_module(library(mark)).
            :- declare(c).
            marked(x).
            local(second).
            """);
        Assert.True(e.Query("declared(c, c), mark_of(x, seen), local_of(second).").Success);
    }

    [Fact]
    public void TheHookIsListedOnce()
    {
        Bundle("mark", MarkLib);
        var e = Engine();
        var listed = new StringWriter();
        e.Out = listed;
        e.ConsultString(":- use_module(library(mark)).");
        Assert.True(e.Query("listing(term_expansion/2).").Success);
        string text = listed.ToString();
        Assert.Equal(1, text.Split("mark_of(").Length - 1);
    }
}
