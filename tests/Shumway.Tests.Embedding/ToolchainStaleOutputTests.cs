using System.Diagnostics;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A failed build leaves no artifact behind.
///
/// <para>What a C toolchain does, and for the same reason: an object or a
/// bundle sitting next to a source that does NOT compile is the one the next
/// link picks up, and the build looks like it worked. Single-file compiles
/// already removed theirs; the paths that compile through the consult
/// pipeline — <c>shumway-compile --consult</c>, and every source path of
/// <c>shumway-link</c> — did not.</para></summary>
public class ToolchainStaleOutputTests
{
    private const string GoodSource = ":- public(main/0).\nmain :- write(hi), nl.\n";
    private const string BrokenSource = ":- public(main/0).\nmain :- write(hi] bad.\n";

    private static string CompileExe => LocateBinary("shumway-compile", "Shumway.Compile");
    private static string LinkExe => LocateBinary("shumway-link", "Shumway.Link");

    private static string LocateBinary(string name, string project)
    {
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string root = LocateRepoRoot();
        string debug = Path.Combine(root, "src", project, "bin", "Debug", "net10.0", name + suffix);
        return File.Exists(debug)
            ? debug
            : Path.Combine(root, "src", project, "bin", "Release", "net10.0", name + suffix);
    }

    private static string LocateRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            if (File.Exists(Path.Combine(current, "Shumway.slnx"))) return current;
            current = Path.GetDirectoryName(current)!;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static int RunCli(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);
        return proc.ExitCode;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"stale-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    [Fact]
    public void CompileViaConsult_FailedBuild_RemovesThePreviousObject()
    {
        if (!File.Exists(CompileExe)) return;
        using var dir = new TempDir();
        string src = Path.Combine(dir.Path, "m1.pl");
        string obj = Path.Combine(dir.Path, "m1.shmo");

        File.WriteAllText(src, "p(1).\np(2).\n");
        Assert.Equal(0, RunCli(CompileExe, "--consult", src));
        Assert.True(File.Exists(obj));

        File.WriteAllText(src, "p(1).\nbroken(] .\n");
        Assert.NotEqual(0, RunCli(CompileExe, "--consult", src));
        Assert.False(File.Exists(obj));
    }

    [Fact]
    public void CompileViaConsult_ObjectNamedByTheModuleDirective_IsRemovedToo()
    {
        // The object is named after the MODULE, not the file, and after a
        // failed consult there is no loaded module to ask.
        if (!File.Exists(CompileExe)) return;
        using var dir = new TempDir();
        string src = Path.Combine(dir.Path, "m2.pl");
        string obj = Path.Combine(dir.Path, "mymod.shmo");

        File.WriteAllText(src, ":- module(mymod).\np(1).\n");
        Assert.Equal(0, RunCli(CompileExe, "--consult", src));
        Assert.True(File.Exists(obj));

        File.WriteAllText(src, ":- module(mymod).\nbroken(] .\n");
        Assert.NotEqual(0, RunCli(CompileExe, "--consult", src));
        Assert.False(File.Exists(obj));
    }

    [Fact]
    public void Link_FailedSourceCompile_RemovesThePreviousBundle()
    {
        if (!File.Exists(LinkExe)) return;
        using var dir = new TempDir();
        string src = Path.Combine(dir.Path, "a.pl");
        string bundle = Path.Combine(dir.Path, "out.shum");

        File.WriteAllText(src, GoodSource);
        Assert.Equal(0, RunCli(LinkExe, "-o", bundle, "-E", "main/0", src));
        Assert.True(File.Exists(bundle));

        File.WriteAllText(src, BrokenSource);
        Assert.NotEqual(0, RunCli(LinkExe, "-o", bundle, "-E", "main/0", src));
        Assert.False(File.Exists(bundle));
    }

    [Fact]
    public void LinkViaConsult_FailedSourceCompile_RemovesThePreviousBundle()
    {
        if (!File.Exists(LinkExe)) return;
        using var dir = new TempDir();
        string src = Path.Combine(dir.Path, "a.pl");
        string bundle = Path.Combine(dir.Path, "out.shum");

        File.WriteAllText(src, GoodSource);
        Assert.Equal(0, RunCli(LinkExe, "--consult", "-o", bundle, "-E", "main/0", src));
        Assert.True(File.Exists(bundle));

        File.WriteAllText(src, BrokenSource);
        Assert.NotEqual(0, RunCli(LinkExe, "--consult", "-o", bundle, "-E", "main/0", src));
        Assert.False(File.Exists(bundle));
    }
}
