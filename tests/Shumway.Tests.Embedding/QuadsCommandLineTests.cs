using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The <c>--quads</c> flag as a test runner: it consults the
/// transcripts, runs them, and ENDS. A run that leaves a prompt behind
/// cannot be scripted, and its verdict has to reach the caller, so the exit
/// code is zero only when every quad passed.
///
/// <para>Each test here holds the child's standard input OPEN and writes
/// nothing to it. That is what makes the claim testable: a top level that
/// stayed interactive would sit there waiting, and the wait is what these
/// assert against.</para></summary>
public sealed class QuadsCommandLineTests
{
    private static string ReplExe
    {
        get
        {
            string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
            string root = RepoRoot();
            foreach (string cfg in new[] { "Release", "Debug" })
            {
                string path = Path.Combine(root, "src", "Shumway.Repl", "bin", cfg,
                                           "net10.0", "shumway" + suffix);
                if (File.Exists(path)) return path;
            }
            throw new InvalidOperationException("shumway was not built.");
        }
    }

    private static string RepoRoot()
    {
        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            if (File.Exists(Path.Combine(current, "Shumway.slnx"))) return current;
            current = Path.GetDirectoryName(current)!;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }

    /// <summary>Runs `shumway --quads &lt;file&gt;` with an input that never
    /// arrives, and returns its exit code. Fails the test if it is still
    /// running after the deadline: that IS the regression.</summary>
    private static int RunQuads(string content, out string output)
    {
        string dir = Path.Combine(Path.GetTempPath(), "quads_cli_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "t_quad.pl");
        File.WriteAllText(file, content);
        var psi = new ProcessStartInfo
        {
            FileName = ReplExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir,
        };
        psi.ArgumentList.Add("--quads");
        psi.ArgumentList.Add(file);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        bool ended = proc.WaitForExit(60_000);
        if (!ended)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            output = "";
            Assert.Fail("--quads did not end: it is still waiting at the top level.");
        }
        output = outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
        int code = proc.ExitCode;
        try { Directory.Delete(dir, recursive: true); } catch { /* leave it */ }
        return code;
    }

    [Fact]
    public void ARunThatPassesEndsWithZero()
    {
        int code = RunQuads("t1\n?- atom(a).\n   true.\n", out string output);
        Assert.Contains("quads: 1/1", output);
        Assert.Equal(0, code);
    }

    [Fact]
    public void AFailingQuadIsAFailingRun()
    {
        // The verdict has to reach a script, and printing it is not reaching.
        int code = RunQuads("t1\n?- atom(a).\n   false.\n", out string output);
        Assert.Contains("quads: 0/1", output);
        Assert.Equal(1, code);
    }

    [Fact]
    public void OnePassingAndOneFailingIsStillAFailingRun()
    {
        int code = RunQuads("t1\n?- atom(a).\n   true.\nt2\n?- atom(a).\n   false.\n",
                            out string output);
        Assert.Contains("quads: 1/2", output);
        Assert.Equal(1, code);
    }

    [Fact]
    public void AFileWithNoQuadsInItIsNotACleanRun()
    {
        // Reporting 0/0 as success is how a wrong file, or a transcript this
        // library did not recognise, would go unnoticed.
        int code = RunQuads("foo(1).\n", out string output);
        Assert.Contains("no quad tests found", output);
        Assert.Equal(1, code);
    }
}
