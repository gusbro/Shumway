using System.Diagnostics;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 14 chunk 170: <c>shumway-compile --verbose</c> now lists
/// every <c>:- public</c> and <c>:- dynamic</c> indicator the
/// module exports, after the per-file compile line. Makes it easy
/// to eyeball "did this file export what I expected?".
/// </summary>
public class Chunk170Tests
{
    private static string CompileExe => LocateBinary("shumway-compile");

    private static string LocateBinary(string name)
    {
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string repoRoot = LocateRepoRoot();
        string path = Path.Combine(repoRoot, "src", "Shumway.Compile", "bin", "Debug",
            "net10.0", name + suffix);
        return path;
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

    private static int RunCli(string exe, out string stderr, params string[] args)
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
        stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return proc.ExitCode;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"chunk170-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    [Fact]
    public void Verbose_ListsPublics()
    {
        if (!File.Exists(CompileExe)) return;
        using var dir = new TempDir();
        string pl = Path.Combine(dir.Path, "lib.pl");
        File.WriteAllText(pl,
            ":- module(lib).\n"
            + ":- public foo/1.\n"
            + ":- public bar/2.\n"
            + "foo(_). bar(_, _).\n"
            + "helper(_).\n");
        int exit = RunCli(CompileExe, out string stderr, "-v", pl);
        Assert.Equal(0, exit);
        Assert.Contains("public", stderr);
        Assert.Contains("foo/1", stderr);
        Assert.Contains("bar/2", stderr);
        // Local predicate must NOT appear under public.
        var publicSection = stderr.Substring(stderr.IndexOf("public"));
        Assert.DoesNotContain("helper/1", publicSection);
    }

    [Fact]
    public void Verbose_ListsDynamics()
    {
        if (!File.Exists(CompileExe)) return;
        using var dir = new TempDir();
        string pl = Path.Combine(dir.Path, "lib.pl");
        File.WriteAllText(pl,
            ":- module(lib).\n"
            + ":- dynamic state/1.\n"
            + ":- dynamic log/2.\n");
        int exit = RunCli(CompileExe, out string stderr, "-v", pl);
        Assert.Equal(0, exit);
        Assert.Contains("dynamic", stderr);
        Assert.Contains("state/1", stderr);
        Assert.Contains("log/2", stderr);
    }

    [Fact]
    public void NonVerbose_DoesNotListPredicates()
    {
        if (!File.Exists(CompileExe)) return;
        using var dir = new TempDir();
        string pl = Path.Combine(dir.Path, "lib.pl");
        File.WriteAllText(pl, ":- module(lib).\n:- public foo/1.\nfoo(_).\n");
        int exit = RunCli(CompileExe, out string stderr, pl);
        Assert.Equal(0, exit);
        // Without -v, the predicate list isn't enumerated.
        Assert.DoesNotContain("foo/1", stderr);
        // But the "compiling X" line still shows.
        Assert.Contains("compiling", stderr, StringComparison.OrdinalIgnoreCase);
    }
}
