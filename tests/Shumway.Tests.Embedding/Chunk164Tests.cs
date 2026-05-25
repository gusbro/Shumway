using System.Diagnostics;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 13 chunk 164: end-to-end of the <c>shumway-compile</c> +
/// <c>shumway-link</c> CLIs by driving them as in-process child
/// processes from compiled <c>.pl</c> sources through <c>.shmo</c>s
/// to a final <c>.shum</c> bundle, loaded into a real
/// <see cref="PrologEngine"/>.
/// </summary>
public class Chunk164Tests
{
    private static string CompileExe => LocateBinary("shumway-compile");
    private static string LinkExe => LocateBinary("shumway-link");

    private static string LocateBinary(string name)
    {
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string repoRoot = LocateRepoRoot();
        // src/Shumway.Compile/bin/Debug/net10.0/shumway-compile(.exe)
        string projectName = name == "shumway-compile" ? "Shumway.Compile" : "Shumway.Link";
        string path = Path.Combine(repoRoot, "src", projectName, "bin", "Debug",
            "net10.0", name + suffix);
        if (!File.Exists(path))
        {
            // Try Release as a fallback.
            path = Path.Combine(repoRoot, "src", projectName, "bin", "Release",
                "net10.0", name + suffix);
        }
        return path;
    }

    private static string LocateRepoRoot()
    {
        // tests/Shumway.Tests.Embedding/bin/Debug/net10.0/Shumway.Tests.Embedding.dll
        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            if (File.Exists(Path.Combine(current, "Shumway.slnx"))) return current;
            current = Path.GetDirectoryName(current)!;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static int RunCli(string exe, params string[] args)
        => RunCli(exe, out _, out _, args);

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
                $"shmo-cli-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Cli_CompileThenLink_LoadAndQuery()
    {
        if (!File.Exists(CompileExe) || !File.Exists(LinkExe))
            return; // CLIs not built — skip silently rather than failing
                    // a unit-test run that didn't build the binaries.

        using var dir = new TempDir();
        string libPl = Path.Combine(dir.Path, "lib.pl");
        string appPl = Path.Combine(dir.Path, "app.pl");
        string libShmo = Path.Combine(dir.Path, "lib.shmo");
        string appShmo = Path.Combine(dir.Path, "app.shmo");
        string outShum = Path.Combine(dir.Path, "out.shum");

        File.WriteAllText(libPl,
            ":- module(lib).\n:- public greet/1.\ngreet(hello).\n");
        File.WriteAllText(appPl,
            ":- module(app).\n:- public main/1.\nmain(X) :- greet(X).\n");

        Assert.Equal(0, RunCli(CompileExe, "-o", libShmo, libPl));
        Assert.Equal(0, RunCli(CompileExe, "-o", appShmo, appPl));
        Assert.True(File.Exists(libShmo));
        Assert.True(File.Exists(appShmo));

        int exit = RunCli(LinkExe, out _, out _,
            "-o", outShum,
            "--entry", "main/1",
            libShmo, appShmo);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(outShum));

        var bundle = BundleReader.ReadFromFile(outShum);
        var engine = new PrologEngine();
        engine.LoadBundle(bundle);
        var sol = engine.Query("main(X).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Cli_Link_MissingPredicate_FailsWithExitOne()
    {
        if (!File.Exists(CompileExe) || !File.Exists(LinkExe))
            return;

        using var dir = new TempDir();
        string pl = Path.Combine(dir.Path, "broken.pl");
        string shmo = Path.Combine(dir.Path, "broken.shmo");
        string output = Path.Combine(dir.Path, "out.shum");
        File.WriteAllText(pl,
            ":- module(m).\n:- public main/0.\nmain :- nonexistent.\n");

        Assert.Equal(0, RunCli(CompileExe, "-o", shmo, pl));
        int exit = RunCli(LinkExe, out _, out string stderr,
            "-o", output, "--entry", "main/0", shmo);
        Assert.Equal(1, exit);
        Assert.Contains("nonexistent", stderr,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cli_Link_AllowUndefined_SucceedsWithWarning()
    {
        if (!File.Exists(CompileExe) || !File.Exists(LinkExe))
            return;

        using var dir = new TempDir();
        string pl = Path.Combine(dir.Path, "loose.pl");
        string shmo = Path.Combine(dir.Path, "loose.shmo");
        string output = Path.Combine(dir.Path, "out.shum");
        File.WriteAllText(pl,
            ":- module(m).\n:- public main/0.\nmain :- maybe_missing.\n");
        Assert.Equal(0, RunCli(CompileExe, "-o", shmo, pl));
        int exit = RunCli(LinkExe,
            "-o", output, "--entry", "main/0",
            "--allow-undefined", shmo);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Cli_Link_MultipleEntryFlags_AndCommaSeparated()
    {
        if (!File.Exists(CompileExe) || !File.Exists(LinkExe))
            return;

        using var dir = new TempDir();
        string pl = Path.Combine(dir.Path, "multi.pl");
        string shmo = Path.Combine(dir.Path, "multi.shmo");
        string output = Path.Combine(dir.Path, "out.shum");
        File.WriteAllText(pl,
            ":- module(m).\n:- public a/0.\n:- public b/0.\n:- public c/0.\n"
            + "a. b. c.\n");
        Assert.Equal(0, RunCli(CompileExe, "-o", shmo, pl));
        int exit = RunCli(LinkExe, out _, out _,
            "-o", output,
            "--entry", "a/0,b/0",
            "--entry", "c/0",
            shmo);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void Cli_Compile_NoArgs_ExitsUsageError()
    {
        if (!File.Exists(CompileExe)) return;
        int exit = RunCli(CompileExe);
        Assert.Equal(3, exit);
    }

    [Fact]
    public void Cli_Link_NoEntries_ExitsUsageError()
    {
        if (!File.Exists(LinkExe)) return;
        using var dir = new TempDir();
        string shmo = Path.Combine(dir.Path, "x.shmo");
        File.WriteAllBytes(shmo, new byte[] { (byte)'S', (byte)'H', (byte)'M', (byte)'O' });
        // No --entry flag → usage error.
        int exit = RunCli(LinkExe,
            "-o", Path.Combine(dir.Path, "out.shum"),
            shmo);
        Assert.Equal(3, exit);
    }
}
