using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-075, decisions 5 and 6: a program linked against a dynamic library
/// (<c>shumway-link --dynlib</c>). The link resolves the program's calls
/// against what the library offers and records what it used; loading the
/// program attaches the library, checks that it still offers that, and brings
/// in the modules that define it before the program's code.
/// </summary>
public sealed class LinkingAgainstALibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_dynlink_" + Guid.NewGuid().ToString("N"));

    public LinkingAgainstALibraryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Ma = @"pa(1).
pa(2).
helper(x).
";
    private const string Mq = @":- module(mq, [q/1]).
q(X) :- hidden(X).
hidden(42).
";
    private const string Mz = @"unused(0).
";
    private const string Md = @":- dynamic(counter/1).
counter(0).
";

    private string BuildLibrary(params (string Module, string Source)[] modules)
    {
        var objects = modules.Select(m => ShmoCompiler.CompileSource(m.Source, m.Module)).ToArray();
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects,
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            AllowUndefined = true,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        string path = Path.Combine(_dir, "lib.shum");
        File.WriteAllBytes(path, BundleWriter.ToBytes(link.Bundle!, includeCompiledBytecode: true));
        return path;
    }

    private string FullLibrary() => BuildLibrary(("ma", Ma), ("mq", Mq), ("mz", Mz), ("md", Md));

    private static LinkResult LinkProgram(string source, string library, params PredicateRef[] entries)
        => ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(source, "prog") },
            EntryPoints = entries,
            DynamicLibraries = library.Length == 0
                ? Array.Empty<LinkDynamicLibrary>()
                : new[] { new LinkDynamicLibrary("lib", BundleReader.ReadFromFile(library)) },
        });

    private string WriteProgram(LinkResult link)
    {
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        string path = Path.Combine(_dir, "prog.shum");
        File.WriteAllBytes(path, link.Bytes!);
        return path;
    }

    private static PrologEngine Engine() => new() { Out = new StringWriter(), Warnings = new StringWriter() };

    private static string Uses(Bundle bundle)
        => string.Join(" ", bundle.DynamicLibraries.Select(l =>
            l.Name + ":[" + string.Join(",", l.Uses.Select(u => $"{u.Module}:{u.Name}/{u.Arity}")) + "]"));

    private const string Program = "main(X, Y) :- pa(X), mq:hidden(Y).\n";

    [Fact]
    public void ACallTheLibraryOffers_Links_AndIsRecorded()
    {
        string lib = FullLibrary();
        Assert.False(LinkProgram(Program, "", new PredicateRef("main", 2)).Success);

        var link = LinkProgram(Program, lib, new PredicateRef("main", 2));
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        Assert.Equal("lib:[:pa/1,mq:hidden/1]", Uses(BundleReader.FromBytes(link.Bytes!)));
        // Both writers write the record.
        Assert.Equal("lib:[:pa/1,mq:hidden/1]",
            Uses(BundleReader.FromBytes(BundleWriter.ToBytes(link.Bundle!))));
    }

    [Fact]
    public void ACallNobodyOffers_StillFailsToLink()
    {
        var link = LinkProgram("main :- nope.\n", FullLibrary(), new PredicateRef("main", 0));
        Assert.False(link.Success);
        Assert.Contains(link.Diagnostics, d => d.Code == "missing_predicate" && d.Message.Contains("nope"));
    }

    [Fact]
    public void AnImportFromAModuleOfTheLibrary_LinksAndRuns()
    {
        string lib = FullLibrary();
        var link = LinkProgram(":- use_module(library(mq)).\nr(X) :- q(X).\n", lib, new PredicateRef("r", 1));
        string prog = WriteProgram(link);
        Assert.Equal("lib:[mq:q/1]", Uses(link.Bundle!));

        var e = Engine();
        e.LoadBundle(prog);
        Assert.True(e.Query("r(X), X == 42.").Success);
    }

    [Fact]
    public void AModuleTheLibraryHas_IsNotLinkedInFromTheSearchPath()
    {
        string lib = FullLibrary();
        string src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "mq.pl"), Mq);
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(":- use_module(library(mq)).\nr(X) :- q(X).\n", "prog") },
            EntryPoints = new[] { new PredicateRef("r", 1) },
            LibraryDirs = new[] { src },
            DynamicLibraries = new[] { new LinkDynamicLibrary("lib", BundleReader.ReadFromFile(lib)) },
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(link.Bundle!.Entries, en => en.ModuleName == "mq");
        Assert.Equal("lib:[mq:q/1]", Uses(link.Bundle!));
    }

    [Fact]
    public void LoadingTheProgram_BringsInWhatItUses_BeforeItsCode()
    {
        string prog = WriteProgram(LinkProgram(Program, FullLibrary(), new PredicateRef("main", 2)));
        var e = Engine();
        e.LoadBundle(prog);
        Assert.False(e.IsCodePending("ma"));
        Assert.False(e.IsCodePending("mq"));
        Assert.True(e.IsCodePending("mz"));
        Assert.True(e.Query("main(X, Y), X == 1, Y == 42.").Success);
    }

    [Fact]
    public void ADynamicPredicateOfTheLibrary_IsDeclaredByTheAttach()
    {
        string lib = FullLibrary();
        var link = LinkProgram("c(X) :- counter(X).\n", lib, new PredicateRef("c", 1));
        string prog = WriteProgram(link);
        Assert.Equal("lib:[:counter/1]", Uses(link.Bundle!));
        var e = Engine();
        e.LoadBundle(prog);
        Assert.True(e.Query("c(X), X == 0.").Success);
    }

    [Fact]
    public void ALibraryThatNoLongerOffersWhatTheProgramUses_FailsTheLoad_NamingIt()
    {
        string prog = WriteProgram(LinkProgram(Program, FullLibrary(), new PredicateRef("main", 2)));
        BuildLibrary(("ma", "helper(x).\n"), ("mq", Mq));
        var e = Engine();
        var ex = Assert.Throws<PrologRuntimeException>(() => e.LoadBundle(prog));
        Assert.Equal("existence_error", ex.Kind);
        Assert.Equal("procedure", ex.Detail);
        Assert.Equal(":(bundle(lib), /(pa, 1))", ex.Value?.ToString());
    }

    [Fact]
    public void AMissingLibrary_FailsTheLoad()
    {
        string prog = WriteProgram(LinkProgram(Program, FullLibrary(), new PredicateRef("main", 2)));
        File.Delete(Path.Combine(_dir, "lib.shum"));
        var e = Engine();
        var ex = Assert.Throws<PrologRuntimeException>(() => e.LoadBundle(prog));
        Assert.Equal("existence_error", ex.Kind);
        Assert.Equal("source_sink", ex.Detail);
        Assert.Equal("bundle(lib)", ex.Value?.ToString());
    }

    // ADR-075, decision 3: `:- ensure_loaded(bundle(x))` in an input means to
    // the linker what --dynlib x means.

    private const string Attaching = ":- ensure_loaded(bundle(lib)).\n" + Program;

    [Fact]
    public void TheDirective_IsRecordedInTheObject_AndReadBack()
    {
        var res = ShmoCompiler.TryCompileSource(Attaching, "prog");
        Assert.NotNull(res.Object);
        Assert.DoesNotContain(res.Warnings, w => w.Message.Contains("ensure_loaded"));
        Assert.Equal(new[] { "lib" }, res.Object!.DynamicLibraries);
        Assert.Equal(new[] { "lib" }, ShmoReader.FromBytes(ShmoWriter.ToBytes(res.Object)).DynamicLibraries);
    }

    [Fact]
    public void AnObjectThatAttachesALibrary_LinksAgainstIt_FoundOnTheSearchPath()
    {
        FullLibrary();
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Attaching, "prog") },
            EntryPoints = new[] { new PredicateRef("main", 2) },
            LibraryDirs = new[] { _dir },
        });
        string prog = WriteProgram(link);
        Assert.Equal("lib:[:pa/1,mq:hidden/1]", Uses(link.Bundle!));

        var e = Engine();
        e.LoadBundle(prog);
        Assert.True(e.Query("main(X, Y), X == 1, Y == 42.").Success);
    }

    [Fact]
    public void AModuleTheAttachedLibraryHas_IsNotLinkedInFromTheSearchPath()
    {
        FullLibrary();
        string src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "mq.pl"), Mq);
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(
                ":- ensure_loaded(bundle(lib)).\n:- use_module(library(mq)).\nr(X) :- q(X).\n", "prog") },
            EntryPoints = new[] { new PredicateRef("r", 1) },
            LibraryDirs = new[] { src, _dir },
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(link.Bundle!.Entries, en => en.ModuleName == "mq");
        Assert.Equal("lib:[mq:q/1]", Uses(link.Bundle!));
    }

    [Fact]
    public void ADependencyFromTheSearchPathThatAttachesALibrary_LinksAgainstIt()
    {
        FullLibrary();
        string src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "helper.pl"),
            ":- module(helper, [first/1]).\n:- ensure_loaded(bundle(lib)).\nfirst(X) :- pa(X), !.\n");
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(
                ":- use_module(library(helper)).\nr(X) :- first(X).\n", "prog") },
            EntryPoints = new[] { new PredicateRef("r", 1) },
            LibraryDirs = new[] { src, _dir },
        });
        string prog = WriteProgram(link);
        Assert.Contains(link.Bundle!.Entries, en => en.ModuleName == "helper");
        Assert.Equal("lib:[:pa/1]", Uses(link.Bundle!));
        var e = Engine();
        e.LoadBundle(prog);
        Assert.True(e.Query("r(X), X == 1.").Success);
    }

    // A member an archive gives the link is read before the search path is,
    // as an input is: what its library has is not linked in from a source.
    [Fact]
    public void AMemberOfAnArchiveThatAttachesALibrary_LinksAgainstIt_BeforeTheSearchPath()
    {
        FullLibrary();
        string src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "mq.pl"), Mq);
        var member = ShmoCompiler.CompileSource(
            ":- public(first/1).\n:- ensure_loaded(bundle(lib)).\n:- use_module(library(mq)).\nfirst(X) :- q(X).\n",
            "helper");
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource("r(X) :- first(X).\n", "prog") },
            Libraries = new[] { new LinkLibrary("helpers.shum", new[] { member }) },
            EntryPoints = new[] { new PredicateRef("r", 1) },
            LibraryDirs = new[] { src, _dir },
        });
        string prog = WriteProgram(link);
        Assert.Contains(link.Bundle!.Entries, en => en.ModuleName == "helper");
        Assert.DoesNotContain(link.Bundle!.Entries, en => en.ModuleName == "mq");
        Assert.Equal("lib:[mq:q/1]", Uses(link.Bundle!));
        var e = Engine();
        e.LoadBundle(prog);
        Assert.True(e.Query("r(X), X == 42.").Success);
    }

    [Fact]
    public void TheDirectiveAndTheOption_NameTheLibraryOnce()
    {
        string lib = FullLibrary();
        var link = LinkProgram(":- ensure_loaded(bundle(lib)).\n" + Program, lib, new PredicateRef("main", 2));
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        Assert.Equal("lib:[:pa/1,mq:hidden/1]", Uses(link.Bundle!));
    }

    [Fact]
    public void ALibraryTheLinkDoesNotFind_FailsIt_OrIsAttachedAtLoadUnderAllowUndefined()
    {
        LinkConfig Config(bool allowUndefined) => new()
        {
            Objects = new[] { ShmoCompiler.CompileSource(Attaching, "prog") },
            EntryPoints = new[] { new PredicateRef("main", 2) },
            AllowUndefined = allowUndefined,
        };
        var failed = ShmoLinker.Link(Config(false));
        Assert.False(failed.Success);
        Assert.Contains(failed.Diagnostics, d => d.Code == "unresolved_dynamic_library"
            && d.Severity == LinkSeverity.Error && d.Message.Contains("'lib'"));

        var link = ShmoLinker.Link(Config(true));
        string prog = WriteProgram(link);
        Assert.Contains(link.Diagnostics, d => d.Code == "unresolved_dynamic_library"
            && d.Severity == LinkSeverity.Warning);
        Assert.Equal("lib:[]", Uses(link.Bundle!));

        FullLibrary();
        var e = Engine();
        e.LoadBundle(prog);
        Assert.True(e.IsCodePending("ma"));
        Assert.True(e.Query("main(X, Y), X == 1, Y == 42.").Success);
    }

    [Fact]
    public void ConsultedForTheLink_TheModuleThatAttaches_RecordsIt_AndTheLibraryGivesNoObject()
    {
        string lib = FullLibrary();
        string src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        string root = Path.Combine(src, "prog.pl");
        File.WriteAllText(root, Attaching);
        var errors = new List<ShmoCompileError>();
        var attached = new Dictionary<string, string>();
        var compiled = ShmoViaConsult.CompileMany(new[] { root }, new[] { _dir }, ShmoBuildMode.Release,
            errors, warnings: new StringWriter(), attachedLibraries: attached);
        Assert.Empty(errors);
        Assert.Equal(new[] { "prog" }, compiled.Select(c => c.ModuleName));
        Assert.Equal(new[] { "lib" }, compiled[0].Object.DynamicLibraries);
        Assert.Equal(Path.GetFullPath(lib), attached["lib"], ignoreCase: true);

        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = compiled.Select(c => c.Object).ToArray(),
            EntryPoints = new[] { new PredicateRef("main", 2) },
            LibraryDirs = new[] { _dir },
        });
        string prog = WriteProgram(link);
        Assert.Equal("lib:[:pa/1,mq:hidden/1]", Uses(link.Bundle!));
        var e = Engine();
        e.LoadBundle(prog);
        Assert.True(e.Query("main(X, Y), X == 1, Y == 42.").Success);
    }

    [Fact]
    public void AnArchiveWhoseMemberAttachesALibrary_AttachesItWhenItLoads()
    {
        FullLibrary();
        string archive = Path.Combine(_dir, "progs.shum");
        File.WriteAllBytes(archive, Librarian.CreateArchive(new[]
        {
            new BundleArchiveMember("prog.shmo", ShmoWriter.ToBytes(ShmoCompiler.CompileSource(Attaching, "prog"))),
        }));
        var e = Engine();
        e.LoadBundle(archive);
        Assert.True(e.IsCodePending("mz"));
        Assert.True(e.Query("prog:main(X, Y), X == 1, Y == 42.").Success);
    }
}
