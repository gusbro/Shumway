using System.Diagnostics;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What file-at-a-time compilation cannot carry, it must refuse
/// rather than write.
///
/// <para>A hook is a CONSULT-TIME concept: the pipeline recognises the head,
/// activates it early and adds it to the engine's expansion aggregate.
/// Compiled one file at a time there is no aggregate to join, so the clause
/// lands as an ordinary predicate and the hook never fires. Measured with
/// the same file: consulted, a later `marker.` expands; compiled, linked and
/// loaded, it does not. A module-qualified head is worse than lost -- read as
/// the term it is, `user:term_expansion(...)` defines a predicate for ':'/2.
/// Both used to produce an object and a note.</para></summary>
public sealed class CompileRefusesLostMeaningTests
{
    private static string CompileExe()
    {
        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            if (File.Exists(Path.Combine(current, "Shumway.slnx")))
            {
                string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
                foreach (string cfg in new[] { "Debug", "Release" })
                {
                    string p = Path.Combine(current, "src", "Shumway.Compile",
                        "bin", cfg, "net10.0", "shumway-compile" + suffix);
                    if (File.Exists(p)) return p;
                }
                return "";
            }
            current = Path.GetDirectoryName(current)!;
        }
        return "";
    }

    private static (int Exit, string Err) Run(string exe, params string[] args)
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
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit(60_000);
        outTask.GetAwaiter().GetResult();
        return (proc.ExitCode, errTask.GetAwaiter().GetResult());
    }

    private static string Write(string dir, string name, string text)
    {
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void AHookAndAQualifiedHeadAreRefused_AndAMentionIsNot()
    {
        // The CLI is a separate build: `dotnet test` does not rebuild it, so
        // this reads whatever binary is on disk. A change to the compiler has
        // to be built before this test means anything (the red counter-proof
        // for it passed green until the exe was rebuilt).
        string exe = CompileExe();
        if (exe.Length == 0) return;   // CLI not built

        string dir = Path.Combine(Path.GetTempPath(),
            "shumway-refuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string hook = Write(dir, "hook.pl",
                "term_expansion(marker, expanded).\ngo.\n");
            string qualified = Write(dir, "qualified.pl",
                "user:term_expansion(marker, expanded).\ngo.\n");
            // Naming a hook is not defining one.
            string mention = Write(dir, "mention.pl",
                "expand_it(T, X) :- catch(term_expansion(T, X), _, fail).\n"
                + "% goal_expansion( appears only here\n");

            string hookObj = Path.Combine(dir, "hook.shmo");
            var r = Run(exe, "-o", hookObj, hook);
            Assert.Equal(1, r.Exit);
            Assert.Contains("term_expansion/2", r.Err);
            // A refused compile must not leave an object a later link would use.
            Assert.False(File.Exists(hookObj), "the refused compile wrote an object");

            string qObj = Path.Combine(dir, "qualified.shmo");
            r = Run(exe, "-o", qObj, qualified);
            Assert.Equal(1, r.Exit);
            Assert.Contains("module-qualified clause head", r.Err);
            Assert.False(File.Exists(qObj), "the refused compile wrote an object");

            string mObj = Path.Combine(dir, "mention.shmo");
            r = Run(exe, "-o", mObj, mention);
            Assert.Equal(0, r.Exit);
            Assert.True(File.Exists(mObj), "a file that only mentions a hook must compile");

            // --consult is the path the error names, and it works.
            r = Run(exe, "--consult", "-o", Path.Combine(dir, "viaconsult"), hook);
            Assert.Equal(0, r.Exit);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
