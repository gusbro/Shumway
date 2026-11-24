using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A library without a module directive defines predicates of user when its
/// source loads. Loaded from its bundle by use_module/1 or ensure_loaded/1, it
/// defines the same (ADR-056, amendment): the top level, any module, another
/// library's compiled code and a clause asserted later all call its predicates
/// by name. consult/1 of the bundle keeps them its module's own (#269).
/// </summary>
public sealed class BareLibraryBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_barelib_" + Guid.NewGuid().ToString("N"));

    public BareLibraryBundleTests()
    {
        Directory.CreateDirectory(_dir);
        Build("base", @"
            callit(G) :- call(G).
            pubbase(x).
            base_calls_ext(Y) :- ext_local(Y).
            ");
        Build("ext", @"
            ext_local(y).
            useb(X) :- pubbase(X).
            ");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Build(string name, string source)
    {
        var obj = ShmoCompiler.CompileSource(source, name);
        byte[] bytes = Librarian.CreateArchive(new[]
        {
            new BundleArchiveMember(name + ".shmo", ShmoWriter.ToBytes(obj)),
        });
        File.WriteAllBytes(Path.Combine(_dir, name + ".shum"), bytes);
    }

    private PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter(), Warnings = new StringWriter() };
        e.AddLibraryDirectory(_dir);
        return e;
    }

    // Each a goal its source answers, from user, from either library, from a
    // module that has nothing to do with them, and through call/1.
    public static TheoryData<string> Calls => new()
    {
        "pubbase(x)",
        "ext_local(y)",
        "base:ext_local(y)",
        "ext:useb(x)",
        "base:base_calls_ext(y)",
        "base:callit(ext_local(y))",
        "foo:pubbase(x)",
        "G = useb(x), call(G)",
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public void ItsPredicatesAreCalledByName_InTheQueriesAfterTheLoad(string goal)
    {
        var e = Engine();
        Assert.True(e.Query("use_module(library(base)), ensure_loaded(library(ext)).").Success);
        Assert.True(e.Query(goal + ".").Success);
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public void ItsPredicatesAreCalledByName_InTheQueryThatLoadsIt(string goal)
    {
        var e = Engine();
        Assert.True(e.Query($"use_module(library(base)), ensure_loaded(library(ext)), {goal}.").Success);
    }

    [Fact]
    public void AClauseAssertedLater_CallsThemByName()
    {
        var e = Engine();
        Assert.True(e.Query("use_module(library(base)), use_module(library(ext)).").Success);
        Assert.True(e.Query("assertz((r(X) :- pubbase(X), ext_local(_))).").Success);
        Assert.True(e.Query("r(x).").Success);
    }

    [Fact]
    public void ConsultingTheBundle_KeepsThemItsModulesOwn()
    {
        var e = Engine();
        string bundle = Path.Combine(_dir, "base.shum").Replace('\\', '/');
        Assert.True(e.Query($"consult('{bundle}').").Success);
        Assert.False(e.Query("catch(pubbase(_), error(existence_error(_, _), _), fail).").Success);
        Assert.True(e.Query("base:pubbase(x).").Success);
    }
}
