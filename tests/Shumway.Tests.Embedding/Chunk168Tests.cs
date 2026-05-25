using System.Diagnostics;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 14 chunk 168: <c>shumway-compile</c> accepts multiple
/// positional source files and emits one <c>.shmo</c> per file.
/// Default-mode output advertises each file being compiled.
/// </summary>
public class Chunk168Tests
{
    private static string CompileExe => LocateBinary("shumway-compile");

    private static string LocateBinary(string name)
    {
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string repoRoot = LocateRepoRoot();
        string projectName = name == "shumway-compile" ? "Shumway.Compile" : "Shumway.Link";
        string path = Path.Combine(repoRoot, "src", projectName, "bin", "Debug",
            "net10.0", name + suffix);
        if (!File.Exists(path))
            path = Path.Combine(repoRoot, "src", projectName, "bin", "Release",
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

    private static int RunCli(string exe, out string stdout, out string stderr, params string[] args)
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
        stdout = proc.StandardOutput.ReadToEnd();
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
                $"chunk168-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    [Fact]
    public void MultiInput_NoOutputFlag_EmitsAlongsideEach()
    {
        if (!File.Exists(CompileExe)) return;
        using var dir = new TempDir();
        string a = Path.Combine(dir.Path, "a.pl");
        string b = Path.Combine(dir.Path, "b.pl");
        File.WriteAllText(a, ":- module(a).\n:- public f/0.\nf.\n");
        File.WriteAllText(b, ":- module(b).\n:- public g/0.\ng.\n");

        int exit = RunCli(CompileExe, out _, out string stderr, a, b);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.ChangeExtension(a, ".shmo")));
        Assert.True(File.Exists(Path.ChangeExtension(b, ".shmo")));
        // Default output mentions both files being compiled.
        Assert.Contains("compiling", stderr);
        Assert.Contains("a.pl", stderr);
        Assert.Contains("b.pl", stderr);
    }

    [Fact]
    public void MultiInput_WithOutputDir_EmitsIntoIt()
    {
        if (!File.Exists(CompileExe)) return;
        using var dir = new TempDir();
        string a = Path.Combine(dir.Path, "a.pl");
        string b = Path.Combine(dir.Path, "b.pl");
        string outDir = Path.Combine(dir.Path, "out");
        File.WriteAllText(a, ":- module(a).\n:- public f/0.\nf.\n");
        File.WriteAllText(b, ":- module(b).\n:- public g/0.\ng.\n");

        int exit = RunCli(CompileExe, out _, out _, "-o", outDir, a, b);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(outDir, "a.shmo")));
        Assert.True(File.Exists(Path.Combine(outDir, "b.shmo")));
    }

    [Fact]
    public void SingleInput_WithOutputFile_StillWorks()
    {
        // Backwards-compatible with chunk 161's single-file CLI shape.
        if (!File.Exists(CompileExe)) return;
        using var dir = new TempDir();
        string a = Path.Combine(dir.Path, "a.pl");
        string outFile = Path.Combine(dir.Path, "renamed.shmo");
        File.WriteAllText(a, ":- module(a).\n:- public f/0.\nf.\n");

        int exit = RunCli(CompileExe, out _, out _, "-o", outFile, a);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(outFile));
    }

    [Fact]
    public void DefaultMode_PrintsCompilingLinePerFile()
    {
        if (!File.Exists(CompileExe)) return;
        using var dir = new TempDir();
        string a = Path.Combine(dir.Path, "a.pl");
        File.WriteAllText(a, ":- module(a).\n:- public f/0.\nf.\n");

        int exit = RunCli(CompileExe, out _, out string stderr, a);
        Assert.Equal(0, exit);
        // Even non-verbose mode now advertises what is being compiled.
        Assert.Contains("compiling", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a.pl", stderr);
    }

    [Fact]
    public void MultiInput_OneFails_OthersStillProduced()
    {
        if (!File.Exists(CompileExe)) return;
        using var dir = new TempDir();
        string good = Path.Combine(dir.Path, "good.pl");
        string bad = Path.Combine(dir.Path, "bad.pl");
        File.WriteAllText(good, ":- module(good).\n:- public f/0.\nf.\n");
        // Malformed :- public — ShmoCompiler will throw.
        File.WriteAllText(bad, ":- module(bad).\n:- public foo.\n");

        int exit = RunCli(CompileExe, out _, out _, good, bad);
        // Exit code reflects worst outcome.
        Assert.Equal(1, exit);
        // The good file still landed.
        Assert.True(File.Exists(Path.ChangeExtension(good, ".shmo")));
    }
}
