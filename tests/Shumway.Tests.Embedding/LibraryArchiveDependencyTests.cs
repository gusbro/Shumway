using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-038 §4: a library's archive holds its own modules, and loading
/// it loads the libraries it imports and does not hold, from wherever
/// <c>library(X)</c> resolves.</summary>
public sealed class LibraryArchiveDependencyTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"shumway-archive-deps-{Guid.NewGuid():N}");

    private string Src => Path.Combine(_dir, "src");
    private string Built => Path.Combine(_dir, "built");

    public LibraryArchiveDependencyTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Built);
        File.WriteAllText(Path.Combine(Src, "base.pl"),
            ":- module(base, [b/1]).\nb(1).\nb(2).\n");
        File.WriteAllText(Path.Combine(Src, "top.pl"),
            ":- module(top, [t/1]).\n:- use_module(library(base)).\nt(X) :- b(X).\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Compiles <paramref name="root"/> with <paramref name="dirs"/>
    /// on the path before its own directory.</summary>
    private static List<(string ModuleName, ShmoObject Object, DateTime SourceTimeUtc, bool IsRoot)>
        Compile(string root, params string[] dirs)
    {
        var errors = new List<ShmoCompileError>();
        var compiled = ShmoViaConsult.CompileMany(
            new[] { root }, dirs, ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        return compiled;
    }

    private List<(string ModuleName, ShmoObject Object, DateTime SourceTimeUtc, bool IsRoot)>
        CompileLibrary(string library, params string[] dirs)
        => Compile(Path.Combine(Src, library + ".pl"), dirs);

    /// <summary>Writes built/<paramref name="library"/>.shum holding the
    /// named modules of <paramref name="compiled"/>.</summary>
    private void Archive(string library,
        IEnumerable<(string ModuleName, ShmoObject Object, DateTime SourceTimeUtc, bool IsRoot)> compiled,
        params string[] modules)
    {
        var members = compiled
            .Where(c => modules.Contains(c.ModuleName))
            .Select(c => new BundleArchiveMember(c.ModuleName + ".shmo", ShmoWriter.ToBytes(c.Object)))
            .ToList();
        Assert.Equal(modules.Length, members.Count);
        File.WriteAllBytes(Path.Combine(Built, library + ".shum"), Librarian.CreateArchive(members, null));
    }

    [Fact]
    public void ACompileRecordsTheLibrariesAModuleImports()
    {
        var top = CompileLibrary("top").Single(c => c.ModuleName == "top").Object;
        var dep = Assert.Single(top.LibraryDeps, d => !d.Baked);
        Assert.Equal("base", dep.LibName);
        // Resolved already: an unfiltered dependency is an import-all the
        // linker would still have to resolve.
        Assert.Contains(new PredicateRef("b", 1), dep.Filter!);
    }

    [Fact]
    public void ACompileEmitsNoObjectForWhatABundleBroughtIn()
    {
        Archive("base", CompileLibrary("base"), "base");
        var compiled = CompileLibrary("top", Built);
        Assert.Equal(new[] { "top" }, compiled.Select(c => c.ModuleName));
    }

    [Fact]
    public void AnArchiveLoadsTheLibraryItLeftOut()
    {
        Archive("base", CompileLibrary("base"), "base");
        Archive("top", CompileLibrary("top", Built), "top");
        var e = new PrologEngine();
        e.AddLibraryDirectory(Built);    // no sources: base can only come from its archive
        e.ConsultString(":- use_module(library(top)).");
        Assert.True(e.Query("findall(X, t(X), L), L == [1, 2].").Success);
    }

    [Fact]
    public void AnArchiveThatHoldsItsDependencyDoesNotLoadItAgain()
    {
        Archive("top", CompileLibrary("top"), "top", "base");
        // A base on the path that would show if it loaded.
        File.WriteAllText(Path.Combine(Built, "base.pl"), ":- module(base, [b/1]).\nb(3).\n");
        var e = new PrologEngine();
        e.AddLibraryDirectory(Built);
        e.ConsultString(":- use_module(library(top)).");
        Assert.True(e.Query("findall(X, t(X), L), L == [1, 2].").Success);
    }

    [Fact]
    public void ArchivesThatImportEachOtherLoad()
    {
        // Built a library at a time: pong with no imports, ping against it,
        // then pong again against ping. Each archive leaves the other out.
        string pong = Path.Combine(Src, "pong.pl");
        File.WriteAllText(pong, ":- module(pong, [pong/1]).\npong(ok).\n");
        Archive("pong", CompileLibrary("pong"), "pong");
        File.WriteAllText(Path.Combine(Src, "ping.pl"),
            ":- module(ping, [ping/1, answer/1]).\n:- use_module(library(pong)).\n"
            + "ping(X) :- pong(X).\nanswer(ok).\n");
        Archive("ping", CompileLibrary("ping", Built), "ping");
        File.WriteAllText(pong,
            ":- module(pong, [pong/1]).\n:- use_module(library(ping)).\npong(X) :- answer(X).\n");
        Archive("pong", CompileLibrary("pong", Built), "pong");

        var e = new PrologEngine();
        e.AddLibraryDirectory(Built);
        e.ConsultString(":- use_module(library(ping)).");
        Assert.True(e.Query("ping(ok).").Success);
    }

    [Fact]
    public void ALibraryCompiledOverABundleLeavesItsDynamicsToIt()
    {
        // rule/1 runs in rules' module: its body calls a local. A copy seeded
        // by uses' archive would take it into uses', where helper/1 is not.
        File.WriteAllText(Path.Combine(Src, "rules.pl"),
            ":- module(rules, [check/1]).\n:- dynamic(rule/1).\n:- dynamic(flag/0).\n"
            + "rule(X) :- helper(X).\nhelper(ok).\ncheck(X) :- rule(X).\n");
        File.WriteAllText(Path.Combine(Src, "uses.pl"),
            ":- module(uses, [go/1]).\n:- use_module(library(rules)).\ngo(X) :- check(X).\n");
        Archive("rules", CompileLibrary("rules"), "rules");
        var compiled = CompileLibrary("uses", Built);
        var uses = Assert.Single(compiled).Object;
        Assert.DoesNotContain(uses.Defined, d => d.Indicator.Name is "rule" or "flag");
        Archive("uses", compiled, "uses");

        var e = new PrologEngine();
        e.AddLibraryDirectory(Built);
        e.ConsultString(":- use_module(library(uses)).");
        Assert.True(e.Query("go(ok).").Success);
        Assert.True(e.Query("findall(X, rules:rule(X), L), L == [ok].").Success);
    }

    [Fact]
    public void ARootABundleAlsoBroughtInIsCompiled()
    {
        // Rebuilding base over its own old bundle, which top's archive
        // brings in while base compiles.
        Archive("base", CompileLibrary("base"), "base");
        Archive("top", CompileLibrary("top", Built), "top");
        File.WriteAllText(Path.Combine(Src, "base.pl"),
            ":- module(base, [b/1]).\n:- use_module(library(top)).\nb(1).\nb(2).\n");
        var compiled = CompileLibrary("base", Built);
        Assert.Contains("base", compiled.Select(c => c.ModuleName));
    }
}
