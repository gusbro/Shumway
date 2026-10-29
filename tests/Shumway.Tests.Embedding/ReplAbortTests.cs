using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>abort/0 at the top level, as the program runs it: a query that
/// aborts says so and the prompt takes the next one; an abort while loading
/// the files of the command line abandons the load, the files after it
/// included, and the top level takes over.</summary>
public sealed class ReplAbortTests
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

    private static string Piped(string queries, params string[] files)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ReplExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (string f in files) psi.ArgumentList.Add(f);
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(queries);
        proc.StandardInput.Close();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
            Assert.Fail("the top level did not end after its input closed.");
        }
        return outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
    }

    [Fact]
    public void AQueryThatAbortsSaysSoAndTheNextOneRuns()
    {
        string output = Piped("abort.\nwrite(alive), nl.\n");

        Assert.Contains("% Execution aborted.", output);
        Assert.Contains("alive", output);
    }

    [Fact]
    public void AnAbortWhileLoadingLeavesTheRestUnloaded()
    {
        string first = Path.Combine(Path.GetTempPath(), "abort_first_" + Guid.NewGuid().ToString("N") + ".pl");
        string second = Path.Combine(Path.GetTempPath(), "abort_second_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(first, ":- initialization(abort).\n");
        File.WriteAllText(second, ":- initialization((write(second_loaded), nl)).\n");
        try
        {
            string output = Piped("write(alive), nl.\n", first, second);

            Assert.Contains("% Execution aborted.", output);
            Assert.DoesNotContain("second_loaded", output);
            Assert.Contains("alive", output);
        }
        finally { File.Delete(first); File.Delete(second); }
    }
}
