using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A buffer loaded as TEXT that pulls in a sibling file. There is no file for
/// the text itself, so there is no directory to resolve against but the current
/// one — which is how a page whose workspace IS the current directory expects
/// `:- use_module('other.pl')` to work.
/// </summary>
public sealed class UseModuleRelativeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shumway_um_" + Guid.NewGuid().ToString("N"));
    private readonly string _wasIn = Directory.GetCurrentDirectory();

    public UseModuleRelativeTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "itch.pl"), "itchy(yes).\n");
        Directory.SetCurrentDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_wasIn);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void WithExtension()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ReconsultString(":- use_module('itch.pl').\nuses(X) :- itchy(X).\n");
        Assert.True(e.Query("uses(yes).").Success);
    }

    [Fact]
    public void WithoutExtension()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ReconsultString(":- use_module('itch').\nuses(X) :- itchy(X).\n");
        Assert.True(e.Query("uses(yes).").Success);
    }

    [Fact]
    public void ConsultOfASiblingWorksTheSameWay()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ReconsultString(":- consult('itch.pl').\nuses(X) :- itchy(X).\n");
        Assert.True(e.Query("uses(yes).").Success);
    }
}

/// <summary>
/// Load-time warnings must be able to reach a host that has no console. A
/// browser page is the case that forced this: the warning existed, went to
/// standard error, and nobody ever saw it.
/// </summary>
public sealed class LoadWarningTests
{
    [Fact]
    public void AMissingUseModuleTargetIsReported()
    {
        var warnings = new StringWriter();
        var e = new PrologEngine { Out = new StringWriter(), Warnings = warnings };
        e.ConsultString(":- use_module('no_such_file_xyz.pl').\np(1).\n");

        Assert.Contains("no_such_file_xyz.pl", warnings.ToString());
        // The rest of the file still loaded — a missing import is a warning.
        Assert.True(e.Query("p(1).").Success);
    }

    [Fact]
    public void AFailedDirectiveIsReported()
    {
        var warnings = new StringWriter();
        var e = new PrologEngine { Out = new StringWriter(), Warnings = warnings };
        e.ConsultString(":- fail.\n");
        Assert.Contains("directive failed", warnings.ToString());
    }

    [Fact]
    public void AnUnknownLibraryIsReported()
    {
        var warnings = new StringWriter();
        var e = new PrologEngine { Out = new StringWriter(), Warnings = warnings };
        e.ConsultString(":- use_module(library(no_such_library_xyz)).\n");
        Assert.Contains("no_such_library_xyz", warnings.ToString());
    }
}

/// <summary>
/// use_module is idempotent — until the file changes.
///
/// <para>Importing a library twice must not consult it twice. But someone
/// editing the file they imported means the opposite by the same act: the page
/// reloads the buffer, its `:- use_module` runs again, and the version on disk
/// is the one that must be running. Both rules are right; the file's own
/// timestamp decides which applies.</para>
/// </summary>
public sealed class UseModuleReloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shumway_rl_" + Guid.NewGuid().ToString("N"));
    private readonly string _wasIn = Directory.GetCurrentDirectory();
    private string Dep => Path.Combine(_dir, "dep.pl");

    public UseModuleReloadTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.SetCurrentDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_wasIn);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static void Write(string path, string text)
    {
        File.WriteAllText(path, text);
        // The stamp is (last write, size); a test that rewrites within the
        // filesystem's timestamp granularity would otherwise look unchanged.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void AChangedDependencyIsPickedUpOnReload()
    {
        const string buffer = ":- use_module('dep.pl').\n";
        var e = new PrologEngine { Out = new StringWriter() };

        Write(Dep, "answer(first).\n");
        e.ReconsultString(buffer);
        Assert.True(e.Query("answer(first).").Success);

        Write(Dep, "answer(second).\n");
        e.ReconsultString(buffer);
        Assert.True(e.Query("answer(second).").Success);
        // Replaced, not added to: the old clause is gone.
        Assert.False(e.Query("answer(first).").Success);
    }

    [Fact]
    public void AnUnchangedDependencyIsNotLoadedTwice()
    {
        const string buffer = ":- use_module('dep.pl').\n";
        var e = new PrologEngine { Out = new StringWriter() };

        Write(Dep, "fact(1).\n");
        e.ReconsultString(buffer);
        e.ReconsultString(buffer);
        e.ReconsultString(buffer);

        var s = e.Query("findall(X, fact(X), L), length(L, N).");
        Assert.True(s.Success);
        Assert.Equal(1L, Assert.IsType<Shumway.Compiler.Ast.IntTerm>(s["N"]!).Value);
    }

    [Fact]
    public void ANewPredicateInTheDependencyBecomesVisible()
    {
        // The reported shape: the dependency gains a predicate, and calling it
        // from the buffer must work after reloading the buffer.
        var e = new PrologEngine { Out = new StringWriter() };

        Write(Dep, "one(1).\n");
        e.ReconsultString(":- use_module('dep.pl').\nuse(X) :- one(X).\n");
        Assert.True(e.Query("use(1).").Success);

        Write(Dep, "one(1).\ntwo(2).\n");
        e.ReconsultString(":- use_module('dep.pl').\nuse(X) :- two(X).\n");
        Assert.True(e.Query("use(2).").Success);
    }
}
