using System;
using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ISO §7.4.2.8 <c>ensure_loaded/1</c> — the last §7.4.2 directive
/// Shumway was missing. Its entire point is idempotence, so the tests pin
/// that against the contrast case (<c>consult/1</c>, which appends).</summary>
public sealed class EnsureLoadedTests
{
    private static string WriteFile(string contents)
    {
        string p = Path.Combine(Path.GetTempPath(),
            "shumway_el_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(p, contents);
        return p;
    }

    private static string Q(string path) => path.Replace("\\", "\\\\");

    private static long CountOf(PrologEngine e, string goal)
    {
        var sol = e.Query($"findall(X, {goal}, L), length(L, N).");
        Assert.True(sol.Success);
        return ((IntTerm)sol["N"]!).Value;
    }

    [Fact]
    public void LoadsAFileNotYetLoaded()
    {
        string f = WriteFile("p(1).\np(2).\n");
        try
        {
            var e = new PrologEngine();
            Assert.True(e.Query($"ensure_loaded('{Q(f)}').").Success);
            Assert.Equal(2, CountOf(e, "p(X)"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void SecondCall_IsANoOp_WhereConsultWouldAppend()
    {
        string f = WriteFile("p(1).\np(2).\n");
        try
        {
            var e = new PrologEngine();
            e.Query($"ensure_loaded('{Q(f)}').");
            e.Query($"ensure_loaded('{Q(f)}').");
            Assert.Equal(2, CountOf(e, "p(X)"));

            // The contrast that makes the assertion above mean something:
            // consult/1 appends, which is exactly what ensure_loaded avoids.
            var e2 = new PrologEngine();
            e2.Query($"consult('{Q(f)}').");
            e2.Query($"consult('{Q(f)}').");
            Assert.Equal(4, CountOf(e2, "p(X)"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void AfterAPlainConsult_EnsureLoadedStillDoesNothing()
    {
        string f = WriteFile("p(1).\n");
        try
        {
            var e = new PrologEngine();
            e.Query($"consult('{Q(f)}').");
            e.Query($"ensure_loaded('{Q(f)}').");
            Assert.Equal(1, CountOf(e, "p(X)"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void AChangedFile_IsReloaded()
    {
        string f = WriteFile("p(1).\n");
        try
        {
            var e = new PrologEngine();
            e.Query($"ensure_loaded('{Q(f)}').");
            Assert.Equal(1, CountOf(e, "p(X)"));

            // Someone edited it: running against a version that no longer
            // exists is the worse outcome, so this reloads (as use_module does).
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddSeconds(-30));
            File.WriteAllText(f, "p(1).\np(2).\np(3).\n");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow);

            e.Query($"ensure_loaded('{Q(f)}').");
            Assert.Equal(3, CountOf(e, "p(X)"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void WorksAsADirectiveInsideAConsultedFile()
    {
        string dep = WriteFile("dep_fact(ok).\n");
        string main = WriteFile($":- ensure_loaded('{Q(dep)}').\nmain_fact(yes).\n");
        try
        {
            var e = new PrologEngine();
            e.ConsultFile(main);
            Assert.True(e.Query("dep_fact(ok).").Success);
            Assert.True(e.Query("main_fact(yes).").Success);

            // Two files each naming the same dependency: it loads once.
            string other = WriteFile($":- ensure_loaded('{Q(dep)}').\n");
            try
            {
                e.ConsultFile(other);
                Assert.Equal(1, CountOf(e, "dep_fact(X)"));
            }
            finally { File.Delete(other); }
        }
        finally { File.Delete(dep); File.Delete(main); }
    }

    [Fact]
    public void UnboundArgument_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(() => e.Query("ensure_loaded(_)."));
        Assert.Contains("instantiation_error", ex.Message);
    }

    [Fact]
    public void NonAtomArgument_RaisesTypeError()
    {
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(() => e.Query("ensure_loaded(42)."));
        Assert.Contains("type_error", ex.Message);
    }

    [Fact]
    public void MissingFile_RaisesExistenceError()
    {
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(
            () => e.Query("ensure_loaded('no_such_file_xyz')."));
        Assert.Contains("existence_error", ex.Message);
    }
}
