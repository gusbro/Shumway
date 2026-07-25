using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-038 Component 1 — library search path + resolver. use_module(library(X))
/// resolves X.pl / X.shum off a per-engine search path fed by the
/// AddLibraryDirectory API, the SHUMWAY_LIBRARY_PATH env var, and the
/// file_search_path(library, Dir) / library_directory(Dir) dynamic facts.
/// </summary>
public class LibrarySearchPathTests
{
    // A throwaway directory holding one .pl library; disposed with the fixture.
    private sealed class LibDir : System.IDisposable
    {
        public string Path { get; }
        public LibDir(string libName, string source)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "shumway-libtest-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, libName + ".pl"), source);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private const string GreetSource = "hello(world).\ngreeting(hi).\n";

    [Fact]
    public void AddLibraryDirectory_ResolvesUseModuleLibrary()
    {
        using var lib = new LibDir("greet", GreetSource);
        var engine = new PrologEngine();
        engine.AddLibraryDirectory(lib.Path);
        engine.ConsultString(":- use_module(library(greet)).");
        Assert.True(engine.Query("hello(world).").Success);
        Assert.True(engine.Query("greeting(hi).").Success);
    }

    // A query that must not throw an existence_error — succeeds iff the goal
    // has a solution, false if it fails OR the predicate is undefined.
    private static bool Holds(PrologEngine e, string goal) =>
        e.Query($"catch(({goal}), _, fail).").Success;

    [Fact]
    public void LibraryDirectoryFact_ResolvesUseModuleLibrary()
    {
        using var lib = new LibDir("greet", GreetSource);
        var engine = new PrologEngine();
        // A library_directory/1 fact asserted before the import populates the
        // search path (SICStus convention). Separate consults force ordering.
        engine.ConsultString(
            $":- assertz(library_directory('{lib.Path.Replace("\\", "/")}')).");
        engine.ConsultString(":- use_module(library(greet)).");
        Assert.True(engine.Query("hello(world).").Success);
    }

    [Fact]
    public void FileSearchPathFact_ResolvesUseModuleLibrary()
    {
        using var lib = new LibDir("greet", GreetSource);
        var engine = new PrologEngine();
        // A file_search_path(library, Dir) fact (SWI/Scryer convention).
        engine.ConsultString(
            $":- assertz(file_search_path(library, '{lib.Path.Replace("\\", "/")}')).");
        engine.ConsultString(":- use_module(library(greet)).");
        Assert.True(engine.Query("hello(world).").Success);
    }

    [Fact]
    public void UnknownLibrary_DoesNotThrow()
    {
        var engine = new PrologEngine();
        // No search path provides 'nosuchlib_xyz'; the import warns and is
        // skipped rather than aborting the consult.
        engine.ConsultString(":- use_module(library(nosuchlib_xyz)).\np(ok).");
        Assert.True(engine.Query("p(ok).").Success);
    }

    [Fact]
    public void BakedLibrary_TakesPrecedenceOverFile()
    {
        // A file named clpfd.pl on the search path must NOT shadow the baked
        // C# CLP(FD) library — the baked switch wins.
        using var lib = new LibDir("clpfd", "shadow_marker(tripped).\n");
        var engine = new PrologEngine();
        engine.AddLibraryDirectory(lib.Path);
        engine.ConsultString(":- use_module(library(clpfd)).");
        // The baked library loaded (its constraint runs); the shadow file was
        // not consulted (shadow_marker/1 is undefined, not merely false).
        Assert.False(Holds(engine, "shadow_marker(tripped)"));
        Assert.True(Holds(engine, "X in 1..3, X #= 2"));
    }

    [Fact]
    public void AbsoluteFileName_ResolvesLibraryAlias()
    {
        using var lib = new LibDir("greet", GreetSource);
        var engine = new PrologEngine();
        engine.AddLibraryDirectory(lib.Path);
        var expected = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(lib.Path, "greet.pl"));
        var sol = engine.Query("absolute_file_name(library(greet), P).");
        Assert.True(sol.Success);
        Assert.Equal(expected, Assert.IsType<AtomTerm>(sol["P"]).Name);
    }

    [Fact]
    public void Idempotent_RepeatedImportLoadsOnce()
    {
        using var lib = new LibDir("greet", GreetSource);
        var engine = new PrologEngine();
        engine.AddLibraryDirectory(lib.Path);
        engine.ConsultString(
            ":- use_module(library(greet)).\n" +
            ":- use_module(library(greet)).");
        // A single solution to hello/1 — the file was not double-consulted.
        Assert.Single(engine.QueryAll("hello(X)."));
    }
}
