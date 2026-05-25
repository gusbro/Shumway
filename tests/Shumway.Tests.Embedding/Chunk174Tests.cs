using System.Diagnostics;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 14 chunk 174: <c>shumway-link --exe</c>. Produces a single-
/// file native executable for the current platform that loads the
/// bundle and runs a user-supplied goal at startup. Mechanism is a
/// shell-out to <c>dotnet publish</c> with <c>PublishSingleFile=true</c>.
/// </summary>
public class Chunk174Tests
{
    private static string LinkExe => LocateBinary("shumway-link");
    private static string CompileExe => LocateBinary("shumway-compile");

    private static string LocateBinary(string name)
    {
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string repoRoot = LocateRepoRoot();
        string projectName = name == "shumway-compile" ? "Shumway.Compile" : "Shumway.Link";
        string path = Path.Combine(repoRoot, "src", projectName, "bin", "Debug",
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

    private static int RunCli(string exe, out string stdout, out string stderr,
        int timeoutMs = 30_000, params string[] args)
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
        proc.WaitForExit(timeoutMs);
        return proc.HasExited ? proc.ExitCode : -1;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"chunk174-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    // ----- Goal validation unit tests (fast, no shell-out) -----

    [Fact]
    public void Goal_BareAtom_ParsesAsZeroArity()
    {
        Assert.True(ExecutableEmitter.TryValidateGoal("main",
            out string normalised, out var head, out _));
        Assert.Equal("main.", normalised);
        Assert.Equal(new PredicateRef("main", 0), head);
    }

    [Fact]
    public void Goal_AtomWithDot_DotStrippedAndRestored()
    {
        Assert.True(ExecutableEmitter.TryValidateGoal("main.",
            out string normalised, out var head, out _));
        Assert.Equal("main.", normalised);
        Assert.Equal(new PredicateRef("main", 0), head);
    }

    [Fact]
    public void Goal_Compound_HeadExtracted()
    {
        Assert.True(ExecutableEmitter.TryValidateGoal("foo(X, 1)",
            out _, out var head, out _));
        Assert.Equal(new PredicateRef("foo", 2), head);
    }

    [Fact]
    public void Goal_Compound_TrailingDot_Accepted()
    {
        Assert.True(ExecutableEmitter.TryValidateGoal("foo(X, 1).",
            out _, out var head, out _));
        Assert.Equal(new PredicateRef("foo", 2), head);
    }

    [Fact]
    public void Goal_Empty_Rejected()
    {
        Assert.False(ExecutableEmitter.TryValidateGoal("",
            out _, out _, out string? err));
        Assert.NotNull(err);
    }

    [Fact]
    public void Goal_OnlyDot_Rejected()
    {
        Assert.False(ExecutableEmitter.TryValidateGoal(".",
            out _, out _, out string? err));
        Assert.NotNull(err);
    }

    [Fact]
    public void Goal_NumberLiteral_Rejected()
    {
        // 42 isn't callable.
        Assert.False(ExecutableEmitter.TryValidateGoal("42",
            out _, out _, out string? err));
        Assert.NotNull(err);
    }

    [Fact]
    public void Goal_SyntaxError_Rejected()
    {
        // Missing closing paren.
        Assert.False(ExecutableEmitter.TryValidateGoal("foo(X, 1",
            out _, out _, out string? err));
        Assert.NotNull(err);
    }

    [Fact]
    public void Goal_Whitespace_BothWrappedTrailingDotForms_Accepted()
    {
        Assert.True(ExecutableEmitter.TryValidateGoal("  main  ",
            out string norm, out _, out _));
        Assert.Equal("main.", norm);
        Assert.True(ExecutableEmitter.TryValidateGoal("  main . ",
            out norm, out _, out _));
        Assert.Equal("main.", norm);
    }

    // ----- CLI argument validation -----

    [Fact]
    public void Cli_ExeWithoutGoal_FailsWithUsageError()
    {
        if (!File.Exists(LinkExe)) return;
        using var dir = new TempDir();
        string shmo = Path.Combine(dir.Path, "dummy.shmo");
        File.WriteAllBytes(shmo, ShmoWriter.ToBytes(
            ShmoCompiler.CompileSource(":- module(m).\n:- public f/0.\nf.\n", "m")));

        int exit = RunCli(LinkExe, out _, out string stderr, 5_000,
            "-o", Path.Combine(dir.Path, "out.shum"),
            "--entry", "f/0",
            "--exe", Path.Combine(dir.Path, "out.exe"),
            shmo);
        Assert.Equal(3, exit);   // usage error.
        Assert.Contains("--goal", stderr);
    }

    [Fact]
    public void Cli_GoalAloneSatisfiesEntryRequirement()
    {
        // No --entry, only --goal → linker accepts and uses the
        // goal's head as the entry root. Don't actually run dotnet
        // publish here — just sanity-check the link succeeds.
        if (!File.Exists(LinkExe) || !File.Exists(CompileExe)) return;
        using var dir = new TempDir();
        string pl = Path.Combine(dir.Path, "m.pl");
        File.WriteAllText(pl, ":- module(m).\n:- public main/0.\nmain.\n");
        string shmo = Path.Combine(dir.Path, "m.shmo");

        int compExit = RunCli(CompileExe, out _, out _, 30_000, "-o", shmo, pl);
        Assert.Equal(0, compExit);

        int linkExit = RunCli(LinkExe, out _, out _, 10_000,
            "-o", Path.Combine(dir.Path, "out.shum"),
            "--goal", "main",
            shmo);
        Assert.Equal(0, linkExit);
    }

    [Fact]
    public void Cli_SelfContainedWithoutExe_FailsWithUsageError()
    {
        if (!File.Exists(LinkExe)) return;
        using var dir = new TempDir();
        int exit = RunCli(LinkExe, out _, out string stderr, 5_000,
            "-o", Path.Combine(dir.Path, "out.shum"),
            "--entry", "f/0",
            "--self-contained",
            "dummy.shmo");
        Assert.Equal(3, exit);
        Assert.Contains("--self-contained", stderr);
    }

    // ----- End-to-end: compile -> link --exe -> run produced exe -----
    // Skipped by default because dotnet publish is slow (~20-40 s)
    // and may not always be available in CI. Set the env var
    // SHUMWAY_RUN_EXE_TESTS=1 to enable.

    [Fact]
    public void Cli_ExeEndToEnd_BuildsAndRuns()
    {
        if (Environment.GetEnvironmentVariable("SHUMWAY_RUN_EXE_TESTS") != "1") return;
        if (!File.Exists(LinkExe) || !File.Exists(CompileExe)) return;

        using var dir = new TempDir();
        string pl = Path.Combine(dir.Path, "app.pl");
        File.WriteAllText(pl,
            ":- module(app).\n:- public main/0.\nmain :- write(hello), nl.\n");
        string shmo = Path.Combine(dir.Path, "app.shmo");
        string shum = Path.Combine(dir.Path, "app.shum");
        string exe = Path.Combine(dir.Path, "app");

        Assert.Equal(0, RunCli(CompileExe, out _, out _, 30_000, "-o", shmo, pl));
        int linkExit = RunCli(LinkExe, out _, out string linkStderr, 120_000,
            "-o", shum,
            "--goal", "main",
            "--exe", exe,
            "-v",
            shmo);
        Assert.True(linkExit == 0, "linker failed: " + linkStderr);

        string producedExe = OperatingSystem.IsWindows() ? exe + ".exe" : exe;
        Assert.True(File.Exists(producedExe), "produced exe not found: " + producedExe);

        int runExit = RunCli(producedExe, out string runStdout, out _, 10_000);
        Assert.Equal(0, runExit);
        Assert.Contains("hello", runStdout);
    }
}
