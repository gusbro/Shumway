using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-075: a name is offered by one module. A module comes in whole, so a
/// program never runs p1 and p2 of one module with p3 of another that defines
/// p2 too: a name two modules offer to a call that names no module is refused
/// before anything of the second comes in, by the link of a library, by the
/// attach, and by a consult after the attach.
/// </summary>
public sealed class DynamicLibraryNameConflictTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_dynlib_names_" + Guid.NewGuid().ToString("N"));

    public DynamicLibraryNameConflictTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static LinkResult Link(params (string Module, string Source)[] modules)
        => ShmoLinker.Link(new LinkConfig
        {
            Objects = modules.Select(m => ShmoCompiler.CompileSource(m.Source, m.Module)).ToArray(),
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            AllowUndefined = true,
        });

    private void BuildLibrary(string name, params (string Module, string Source)[] modules)
    {
        var link = Link(modules);
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        File.WriteAllBytes(Path.Combine(_dir, name + ".shum"), BundleWriter.ToBytes(link.Bundle!, includeCompiledBytecode: true));
    }

    private PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter(), Warnings = new StringWriter() };
        e.AddLibraryDirectory(_dir);
        return e;
    }

    private const string A = "p1(a).\np2(a).\n";
    private const string B = "p2(b).\np3(X) :- p2(X).\n";

    private const string Refused =
        "catch(ensure_loaded(bundle(lb)), error(permission_error(import, procedure, M:PI), _), true), "
        + "M == b, PI == p2/1";

    // Module a offers p1 and p2, module b of another library p2 and p3.
    [Fact]
    public void ASecondLibraryWithANameTheFirstOffers_IsNotAttached()
    {
        BuildLibrary("la", ("a", A));
        BuildLibrary("lb", ("b", B));
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(la)), p1(X), X == a.").Success);
        Assert.True(e.Query(Refused + ".").Success);
        Assert.Contains("module b defines p2/1, which module a of bundle(la) defines",
            e.Warnings!.ToString());

        // Nothing of lb is in, and la answers as before.
        Assert.False(e.IsCodePending("b"));
        Assert.True(e.Query("catch(p3(_), error(existence_error(procedure, PI), _), true), PI == p3/1.").Success);
        Assert.True(e.Query("findall(X, p2(X), L), L == [a].").Success);

        // Refused again, and not taken for attached.
        Assert.True(e.Query(Refused + ".").Success);
    }

    // The same before anything of the first came in, and in one query.
    [Fact]
    public void TheRefusalDoesNotDependOnWhatCameIn()
    {
        BuildLibrary("la", ("a", A));
        BuildLibrary("lb", ("b", B));
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(la)), " + Refused + ".").Success);
        Assert.True(e.IsCodePending("a"));
    }

    [Fact]
    public void TheLinkOfALibraryWhoseModulesShareAName_Fails()
    {
        var link = Link(("a", A), ("b", B));
        Assert.False(link.Success);
        Assert.Contains(link.Diagnostics, d => d.Code == "shared_library_name" && d.Message.Contains("p2/1"));
    }

    [Fact]
    public void ALibraryWithANameTheProgramDefines_IsNotAttached()
    {
        BuildLibrary("lb", ("b", B));
        var e = Engine();
        e.ConsultString("p2(mine).\n");
        Assert.True(e.Query(Refused + ".").Success);
        Assert.True(e.Query("findall(X, p2(X), L), L == [mine].").Success);
    }

    // After the attach, a consulted clause for a name the library offers is
    // refused, and the rest of the file loads.
    [Fact]
    public void AConsultAfterTheAttach_DoesNotDefineANameTheLibraryOffers()
    {
        BuildLibrary("lb", ("b", B));
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(lb)).").Success);
        e.ConsultString("p2(mine).\nother(1).\n");
        Assert.Contains("p2/1 is defined by module b of bundle(lb); clause ignored", e.Warnings!.ToString());
        Assert.True(e.Query("findall(X, p3(X), L), L == [b], other(1).").Success);
    }

    // A dynamic predicate two libraries give clauses to is one predicate.
    [Fact]
    public void ADynamicPredicateOfTwoLibraries_HasTheClausesOfBoth()
    {
        BuildLibrary("la", ("a", ":- dynamic(t/1).\nt(a).\n"));
        BuildLibrary("lb", ("b", ":- dynamic(t/1).\nt(b).\n"));
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(la)), ensure_loaded(bundle(lb)), findall(X, t(X), L), L == [a, b].").Success);
    }

    [Fact]
    public void ANameOneLibraryHasStaticAndTheOtherDynamic_IsRefused()
    {
        BuildLibrary("la", ("a", ":- dynamic(p2/1).\np2(a).\n"));
        BuildLibrary("lb", ("b", B));
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(la)), " + Refused + ".").Success);
    }

    // The predicates of a module with a module directive are its own: two
    // libraries may use the same names inside their modules, and export the
    // same name, which an import then chooses.
    [Fact]
    public void ModulesWithAModuleDirective_ShareNoName()
    {
        BuildLibrary("la", ("ma", ":- module(ma, [q/1]).\nq(X) :- h(X).\nh(1).\n"));
        BuildLibrary("lb", ("mb", ":- module(mb, [q/1]).\nq(X) :- h(X).\nh(2).\n"));
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(la)), ensure_loaded(bundle(lb)).").Success);
        Assert.True(e.Query("ma:q(X), X == 1, mb:q(Y), Y == 2.").Success);
        Assert.True(e.Query("use_module(library(mb)), q(X), X == 2.").Success);
    }
}
