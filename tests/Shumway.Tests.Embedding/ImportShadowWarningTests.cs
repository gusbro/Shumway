using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Top-level imports win over bare-global publics, so loading two
/// libraries with overlapping surfaces silently reroutes bare calls (the
/// clpfd + clpz coexistence surprise). The engine now warns — aggregated,
/// on stderr — in both load orders, and on import-vs-import collisions.
/// Normal single-source loads stay quiet.</summary>
public sealed class ImportShadowWarningTests
{
    private sealed class LibDir : System.IDisposable
    {
        public string Dir { get; }
        public LibDir()
        {
            Dir = Path.Combine(Path.GetTempPath(),
                "shumway-shadowtest-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }
        public LibDir Add(string name, string source)
        {
            File.WriteAllText(Path.Combine(Dir, name + ".pl"), source);
            return this;
        }
        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static string CaptureStderr(System.Action act)
    {
        var sink = new StringWriter();
        var old = System.Console.Error;
        System.Console.SetError(sink);
        try { act(); } finally { System.Console.SetError(old); }
        return sink.ToString();
    }

    private const string LegacyGlobal =
        ":- module(globmod).\n:- public foo/1.\nfoo(global).\n";
    private const string LibExporting =
        ":- module(liba, [foo/1]).\nfoo(from_liba).\n";
    private const string OtherLibExporting =
        ":- module(libb, [foo/1]).\nfoo(from_libb).\n";

    [Fact]
    public void ImportShadowingAGlobalPublic_Warns_ImportWins()
    {
        using var libs = new LibDir().Add("liba", LibExporting);
        var e = new PrologEngine();
        e.AddLibraryDirectory(libs.Dir);
        e.ConsultString(LegacyGlobal);
        string err = CaptureStderr(() =>
            Assert.True(e.Query("use_module(library(liba)).").Success));
        Assert.Contains("foo/1", err);
        Assert.Contains("shadows the global", err);
        Assert.Contains("globmod", err);
        // The import wins for bare calls.
        Assert.True(e.Query("foo(X), X == from_liba.").Success);
    }

    [Fact]
    public void GlobalPublicLandingUnderAnImport_Warns_ImportStillWins()
    {
        using var libs = new LibDir().Add("liba", LibExporting);
        var e = new PrologEngine();
        e.AddLibraryDirectory(libs.Dir);
        Assert.True(e.Query("use_module(library(liba)).").Success);
        string err = CaptureStderr(() => e.ConsultString(LegacyGlobal));
        Assert.Contains("foo/1", err);
        Assert.Contains("shadowed at the top level", err);
        Assert.Contains("liba", err);
        Assert.True(e.Query("foo(X), X == from_liba.").Success);
    }

    [Fact]
    public void ImportImportCollision_Warns_FirstImportWins()
    {
        using var libs = new LibDir()
            .Add("liba", LibExporting)
            .Add("libb", OtherLibExporting);
        var e = new PrologEngine();
        e.AddLibraryDirectory(libs.Dir);
        Assert.True(e.Query("use_module(library(liba)).").Success);
        string err = CaptureStderr(() =>
            Assert.True(e.Query("use_module(library(libb)).").Success));
        Assert.Contains("already imported from 'liba'", err);
        Assert.True(e.Query("foo(X), X == from_liba.").Success);
    }

    [Fact]
    public void SingleSourceLoads_StayQuiet()
    {
        using var libs = new LibDir().Add("liba", LibExporting);
        var e = new PrologEngine();
        e.AddLibraryDirectory(libs.Dir);
        string err = CaptureStderr(() =>
        {
            Assert.True(e.Query("use_module(library(liba)).").Success);
            // Re-import of the same library is idempotent and silent.
            Assert.True(e.Query("use_module(library(liba)).").Success);
        });
        Assert.DoesNotContain("warning", err);
    }
}
