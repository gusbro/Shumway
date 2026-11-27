using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// An export-qualified module imported from a bundle, not from source.
///
/// <para>Compiling a library to a <c>.shum</c> is what makes a slow one
/// usable — Scryer's clpz loads about five times faster that way. It did not
/// work: the module loaded (its operators even arrived, so the program
/// parsed) but its predicates resolved to nothing, because two things only
/// the source path did were missing. The importer learns which module it
/// imported from "the module this consult declared", which a bundle never
/// sets; and a module's exports are matched against the clauses it defines,
/// which a bundle has none of — it has compiled code.</para>
/// </summary>
public sealed class BundleLibraryImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_libbundle_" + Guid.NewGuid().ToString("N"));

    public BundleLibraryImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Compiles a one-module library to a .shum in the library dir.</summary>
    private string BuildLibrary(string name, string source)
    {
        var obj = ShmoCompiler.CompileSource(source, name);
        byte[] bytes = Librarian.CreateArchive(new[]
        {
            new BundleArchiveMember(name + ".shmo", ShmoWriter.ToBytes(obj)),
        });
        string path = Path.Combine(_dir, name + ".shum");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ItsExportedPredicatesAreCallable()
    {
        BuildLibrary("greetlib",
            ":- module(greetlib, [greet/1]).\ngreet(hello).\n");

        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);
        e.ConsultString("""
            :- use_module(library(greetlib)).
            use(X) :- greet(X).
            """);

        Assert.True(e.Query("use(hello).").Success);
    }

    [Fact]
    public void WhatItDoesNotExportStaysItsOwn()
    {
        BuildLibrary("hidelib",
            ":- module(hidelib, [shown/1]).\nshown(yes).\nhidden(yes).\n");

        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);
        e.ConsultString(":- use_module(library(hidelib)).");

        Assert.True(e.Query("shown(yes).").Success);
        Assert.False(e.Query("catch(hidden(yes), _, fail).").Success);
    }

    // A query that imports a library calls it in the same query, as it does
    // when the library is a source: a notebook's server is one query, so a
    // library loaded by a cell was never callable there.
    [Fact]
    public void ItsPredicatesAreCallableInTheQueryThatImportsIt()
    {
        BuildLibrary("livelib", ":- module(livelib, [live/1]).\nlive(yes).\n");

        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);

        Assert.True(e.Query("use_module(library(livelib)), live(yes).").Success);
        Assert.True(e.Query("live(yes).").Success);
    }

    [Fact]
    public void WhatItCallsOfItsOwn_AndAMetaCall_WorkInThatQuery()
    {
        BuildLibrary("chainlib",
            ":- module(chainlib, [top/1]).\ntop(X) :- inner(X).\ninner(ok).\n");

        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);

        Assert.True(e.Query("use_module(library(chainlib)), top(ok), G = top(Y), call(G), Y == ok.").Success);
    }

    // A bundle compiled file by file carries its initialization goals and runs
    // them where it loads. Loaded by a running goal, they run once what the
    // bundle defines is linked into that goal: one that calls the file's own
    // predicate found nothing before.
    [Theory]
    [InlineData("ensure_loaded(library(initlib))")]
    [InlineData("use_module(library(initlib))")]
    [InlineData("consult('{0}')")]
    public void ItsInitializationGoals_CallWhatItDefines_WhenAGoalLoadsIt(string load)
    {
        string bundle = BuildLibrary("initlib", @"
            :- dynamic(done/1).
            init :- assertz(done(yes)).
            :- initialization(init).
            ");
        var said = new StringWriter();
        var e = new PrologEngine { Out = new StringWriter(), Warnings = said };
        e.AddLibraryDirectory(_dir);

        Assert.True(e.Query(string.Format(load, bundle.Replace('\\', '/')) + ", done(yes).").Success);
        Assert.True(e.Query("findall(X, done(X), [yes]).").Success);
        Assert.Equal("", said.ToString());
    }

    /// <summary>A library's source and its bundle in one directory, the
    /// bundle answering which(bundle) and the source which(source), with the
    /// bundle written <paramref name="bundleAge"/> after the source.</summary>
    private void SourceAndBundle(string name, System.TimeSpan bundleAge, bool otherFormat = false)
    {
        string bundle = BuildLibrary(name, $":- module({name}, [which/1]).\nwhich(bundle).\n");
        if (otherFormat)
        {
            byte[] bytes = File.ReadAllBytes(bundle);
            bytes[8] ^= 0xFF;   // the format stamp follows the magic and the version
            File.WriteAllBytes(bundle, bytes);
        }
        string source = Path.Combine(_dir, name + ".pl");
        File.WriteAllText(source, $":- module({name}, [which/1]).\nwhich(source).\n");
        var t = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, t);
        File.SetLastWriteTimeUtc(bundle, t + bundleAge);
    }

    private string WhichLoads(string name) => WhichLoads(name, out _);

    private string WhichLoads(string name, out string warnings)
    {
        var said = new StringWriter();
        var e = new PrologEngine { Out = new StringWriter(), Warnings = said };
        e.AddLibraryDirectory(_dir);
        var answer = e.Query($"use_module(library({name})), which(W).");
        Assert.True(answer.Success);
        warnings = said.ToString();
        return answer.Get<string>("W");
    }

    [Fact]
    public void BesideItsSource_ABundleNoOlderThanIt_IsTheLibrary()
    {
        SourceAndBundle("newerbundle", System.TimeSpan.FromMinutes(1));
        Assert.Equal("bundle", WhichLoads("newerbundle"));
        SourceAndBundle("samebundle", System.TimeSpan.Zero);
        Assert.Equal("bundle", WhichLoads("samebundle"));
    }

    [Fact]
    public void BesideItsSource_ABundleOlderThanIt_IsNot()
    {
        SourceAndBundle("olderbundle", System.TimeSpan.FromMinutes(-1));
        Assert.Equal("source", WhichLoads("olderbundle"));
    }

    // ADR-076: a bundle of another format is still the library. One that says
    // nothing of how it was built (a host wrote it) is refused, and the
    // source beside it is not loaded in its place.
    [Fact]
    public void BesideItsSource_ABundleOfAnotherFormat_IsRefused_WhenNothingSaysHowItWasBuilt()
    {
        SourceAndBundle("stalebundle", System.TimeSpan.FromMinutes(1), otherFormat: true);
        var said = new StringWriter();
        var e = new PrologEngine { Out = new StringWriter(), Warnings = said };
        e.AddLibraryDirectory(_dir);
        var loaded = e.Query("catch(use_module(library(stalebundle)), Error, true).");
        Assert.True(loaded.Success);
        Assert.Contains("written by a host", said + " " + loaded["Error"]);
        Assert.False(e.Query("catch(which(_), _, fail).").Success);
    }

    // Two bundles give clauses to one dynamic predicate, as an application
    // gives its entries to the tables of a runtime loaded before it. The goal
    // that loads the second sees its clauses, as it does for a source.
    [Fact]
    public void ClausesASecondBundleGivesADynamic_AreSeenInTheQueryThatLoadsIt()
    {
        BuildLibrary("seedbase", ":- dynamic(t/1).\nt(base).\n");
        BuildLibrary("seedext", ":- dynamic(t/1).\n:- multifile(t/1).\nt(ext).\n");

        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);

        Assert.True(e.Query("ensure_loaded(library(seedbase)), ensure_loaded(library(seedext)), "
            + "findall(X, t(X), L), L == [base, ext].").Success);
        Assert.True(e.Query("findall(X, t(X), [base, ext]).").Success);
    }

    // The same when the predicate was there before the query began.
    [Fact]
    public void ClausesABundleGivesADynamicTheQueryStartedWith_AreSeenInThatQuery()
    {
        BuildLibrary("seedfirst", ":- dynamic(t/1).\nt(first).\n");
        BuildLibrary("seedlater", ":- dynamic(t/1).\n:- multifile(t/1).\nt(later).\n");

        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);

        Assert.True(e.Query("ensure_loaded(library(seedfirst)), t(first).").Success);
        Assert.True(e.Query("ensure_loaded(library(seedlater)), "
            + "findall(X, t(X), L), L == [first, later].").Success);
    }

    [Fact]
    public void ABundleConsultedInAQuery_IsCalledInThatQuery()
    {
        string bundle = BuildLibrary("consultlib", ":- module(consultlib, [here/1]).\nhere(now).\n");

        var e = new PrologEngine { Out = new StringWriter() };

        Assert.True(e.Query($"consult('{bundle.Replace('\\', '/')}'), here(now).").Success);
        Assert.True(e.Query("here(now).").Success);
    }

    /// <summary>A library and the library it uses, compiled to one bundle as
    /// the page compiles one: the second is a dependency of the first.</summary>
    private string BuildLibraryWithDependency()
    {
        string sources = Path.Combine(_dir, "src");
        Directory.CreateDirectory(sources);
        File.WriteAllText(Path.Combine(sources, "helperlib.pl"),
            ":- module(helperlib, [helped/1]).\nhelped(yes).\n");
        string root = Path.Combine(sources, "toplib.pl");
        File.WriteAllText(root,
            ":- module(toplib, [top/1]).\n:- use_module(library(helperlib)).\ntop(X) :- helped(X).\n");
        var errors = new List<ShmoCompileError>();
        var compiled = ShmoViaConsult.CompileMany(new[] { root }, new[] { sources }, ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        Assert.Equal(2, compiled.Count);
        byte[] bytes = Librarian.CreateArchive(compiled
            .Select(c => new BundleArchiveMember(c.ModuleName + ".shmo", ShmoWriter.ToBytes(c.Object)))
            .ToList());
        string path = Path.Combine(_dir, "toplib.shum");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // As a module file consulted directly: its exports reach user, and what it
    // uses stays its own, as a source's use_module dependencies do.
    [Fact]
    public void ABundleConsulted_GivesUserItsModulesExports_NotTheirDependencies()
    {
        string bundle = BuildLibraryWithDependency();

        var e = new PrologEngine { Out = new StringWriter() };

        Assert.True(e.Query($"consult('{bundle.Replace('\\', '/')}'), top(yes).").Success);
        Assert.False(e.Query("catch(helped(yes), _, fail).").Success);
        Assert.True(e.Query("helperlib:helped(yes).").Success);
    }

    // A legacy module (module/1 and public/1) is not export-qualified: its
    // publics are global names already, and nothing is imported for it.
    [Fact]
    public void ALegacyModuleOfABundleConsultedInAQuery_KeepsItsPublicsGlobalAndItsPrivatesItsOwn()
    {
        string bundle = BuildLibrary("oldlib",
            ":- module(oldlib).\n:- public(old/1).\nold(X) :- secret(X).\nsecret(yes).\n");

        var e = new PrologEngine { Out = new StringWriter() };

        Assert.True(e.Query($"consult('{bundle.Replace('\\', '/')}'), old(yes).").Success);
        Assert.True(e.Query("old(yes).").Success);
        Assert.False(e.Query("catch(secret(yes), _, fail).").Success);
        Assert.True(e.Query("oldlib:secret(yes).").Success);
    }

    // One bundle, a module/2 library and a legacy one: the first's exports
    // reach user, the legacy one stays as it was.
    [Fact]
    public void ABundleOfAModernAndALegacyModule_ImportsOnlyTheModernExports()
    {
        var modern = ShmoCompiler.CompileSource(
            ":- module(newlib, [fresh/1]).\nfresh(yes).\nkept(yes).\n", "newlib");
        var legacy = ShmoCompiler.CompileSource(
            ":- module(agedlib).\n:- public(aged/1).\naged(yes).\nhidden_aged(yes).\n", "agedlib");
        byte[] bytes = Librarian.CreateArchive(new[]
        {
            new BundleArchiveMember("newlib.shmo", ShmoWriter.ToBytes(modern)),
            new BundleArchiveMember("agedlib.shmo", ShmoWriter.ToBytes(legacy)),
        });
        string bundle = Path.Combine(_dir, "mixed.shum");
        File.WriteAllBytes(bundle, bytes);

        var e = new PrologEngine { Out = new StringWriter() };

        Assert.True(e.Query($"consult('{bundle.Replace('\\', '/')}'), fresh(yes), aged(yes).").Success);
        Assert.False(e.Query("catch(kept(yes), _, fail).").Success);
        Assert.False(e.Query("catch(hidden_aged(yes), _, fail).").Success);
        Assert.True(e.Query("newlib:kept(yes), agedlib:hidden_aged(yes).").Success);
    }

    [Fact]
    public void ABundleTheHostConsults_GivesUserItsModulesExports()
    {
        string bundle = BuildLibraryWithDependency();

        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultFile(bundle);

        Assert.True(e.Query("top(yes).").Success);
        Assert.False(e.Query("catch(helped(yes), _, fail).").Success);
    }

    [Fact]
    public void ALibraryImportedByAConsultInAQuery_IsCalledByWhatThatConsultDefines()
    {
        BuildLibrary("deeplib", ":- module(deeplib, [deep/1]).\ndeep(down).\n");
        string user = Path.Combine(_dir, "user_of_deeplib.pl");
        File.WriteAllText(user, ":- use_module(library(deeplib)).\nreach(X) :- deep(X).\n");

        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);

        Assert.True(e.Query($"consult('{user.Replace('\\', '/')}'), reach(down).").Success);
    }

    [Fact]
    public void SelectedImportsWorkToo()
    {
        BuildLibrary("piecelib",
            ":- module(piecelib, [one/1, two/1]).\none(1).\ntwo(2).\n");

        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);
        e.ConsultString(":- use_module(library(piecelib), [one/1]).");

        Assert.True(e.Query("one(1).").Success);
        Assert.False(e.Query("catch(two(2), _, fail).").Success);
    }
}
