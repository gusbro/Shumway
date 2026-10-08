using System.Diagnostics;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What file-at-a-time compilation cannot carry, it must refuse
/// rather than write.
///
/// <para>A hook is a consult-time concept: the pipeline recognises the head,
/// activates it early and adds it to the engine's expansion aggregate.
/// Compiled one file at a time there is no aggregate to join, so the clause
/// lands as an ordinary predicate and the hook never fires. Measured with
/// the same file: consulted, a later `marker.` expands; compiled, linked and
/// loaded, it does not. A module-qualified head is worse than lost -- read as
/// the term it is, `user:term_expansion(...)` defines a predicate for ':'/2.
/// Both used to produce an object and a note. shumway-link compiles a source
/// it is given the same way, so it refuses the same files.</para></summary>
public sealed class CompileRefusesLostMeaningTests
{
    private static string Exe(string project, string name)
    {
        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            if (File.Exists(Path.Combine(current, "Shumway.slnx")))
            {
                string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
                foreach (string cfg in new[] { "Debug", "Release" })
                {
                    string p = Path.Combine(current, "src", project,
                        "bin", cfg, "net10.0", name + suffix);
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
        string exe = Exe("Shumway.Compile", "shumway-compile");
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

    [Fact]
    public void TheLinkerRefusesTheSameSources()
    {
        // A separate build too: see above.
        string exe = Exe("Shumway.Link", "shumway-link");
        if (exe.Length == 0) return;   // CLI not built

        string dir = Path.Combine(Path.GetTempPath(),
            "shumway-refuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string hook = Write(dir, "hook.pl",
                "term_expansion(marker, expanded).\nmain.\n");
            string qualified = Write(dir, "qualified.pl",
                "user:term_expansion(marker, expanded).\nmain.\n");
            string mention = Write(dir, "mention.pl",
                "main :- T = term_expansion(a, b), functor(T, _, 2).\n"
                + "% goal_expansion( appears only here\n");

            // A refused link must not leave an older bundle behind either.
            string hookOut = Write(dir, "hook.shum", "stale");
            var r = Run(exe, "--entry", "main/0", "-o", hookOut, hook);
            Assert.Equal(1, r.Exit);
            Assert.Contains("term_expansion/2", r.Err);
            Assert.False(File.Exists(hookOut), "the refused link left a bundle");

            string qOut = Path.Combine(dir, "qualified.shum");
            r = Run(exe, "--entry", "main/0", "-o", qOut, qualified);
            Assert.Equal(1, r.Exit);
            Assert.Contains("module-qualified clause head", r.Err);
            Assert.False(File.Exists(qOut), "the refused link wrote a bundle");

            string mOut = Path.Combine(dir, "mention.shum");
            r = Run(exe, "--entry", "main/0", "-o", mOut, mention);
            Assert.True(r.Exit == 0, r.Err);
            Assert.True(File.Exists(mOut), "a file that only mentions a hook must link");

            // --consult is the path the error names, and it works.
            r = Run(exe, "--consult", "--entry", "main/0", "-o", Path.Combine(dir, "viaconsult.shum"), hook);
            Assert.True(r.Exit == 0, r.Err);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
