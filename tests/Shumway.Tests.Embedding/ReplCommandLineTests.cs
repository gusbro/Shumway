using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The top level's command line as a process sees it: -q, repeated
/// -g goals, a -t goal in the interactive loop's place, and a top level saved
/// with --save-toplevel. Each run gets its input up front and is killed if it
/// outlives the deadline, which is how a top level that stayed interactive
/// shows up.</summary>
public sealed class ReplCommandLineTests
{
    // The REPL built in the test assembly's own configuration: a Release REPL
    // left over from another build would answer for this one's sources.
    private static string ReplExe => Binary("Shumway.Repl", "shumway");

    private static string Binary(string project, string name)
    {
        char sep = Path.DirectorySeparatorChar;
        string cfg = AppContext.BaseDirectory.Contains($"{sep}Release{sep}") ? "Release" : "Debug";
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string path = Path.Combine(RepoRoot(), "src", project, "bin", cfg, "net10.0", name + suffix);
        if (!File.Exists(path)) throw new InvalidOperationException($"{name} was not built: {path}");
        return path;
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

    private static int Run(string exe, string dir, string input, out string output, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(input);
        proc.StandardInput.Close();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Assert.Fail($"{Path.GetFileName(exe)} {string.Join(' ', args)} did not end.");
        }
        output = outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
        return proc.ExitCode;
    }

    private sealed class TempDir : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "repl_cli_" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string name, string text)
        {
            string p = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(p, text);
            return p;
        }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    [Fact]
    public void QuietLeavesOutTheBannerAndTheLoadMessages_AndTheVerboseFlagSaysSo()
    {
        using var dir = new TempDir();
        dir.File("a.pl", "p(1).\n");
        const string query = "current_prolog_flag(verbose, V), write(v(V)), nl.\nhalt.\n";
        Assert.Equal(0, Run(ReplExe, dir.Path, query, out string loud, "a.pl"));
        Assert.Contains("Shumway Prolog", loud);
        Assert.Contains("% consulted a.pl", loud);
        Assert.Contains("v(normal)", loud);
        Assert.Equal(0, Run(ReplExe, dir.Path, query, out string quiet, "-q", "a.pl"));
        Assert.DoesNotContain("Shumway Prolog", quiet);
        Assert.DoesNotContain("% consulted", quiet);
        Assert.Contains("v(silent)", quiet);
    }

    [Fact]
    public void GoalsRunInTheOrderGiven_ThenTheTopLevelGoalEndsTheRun()
    {
        using var dir = new TempDir();
        int code = Run(ReplExe, dir.Path, "", out string output,
            "-g", "write(first), nl", "-g", "write(second), nl", "-t", "halt");
        Assert.Equal(0, code);
        int first = output.IndexOf("first", StringComparison.Ordinal);
        int second = output.IndexOf("second", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, output);
        // A -t goal runs in the interactive session's place: no banner opens one.
        Assert.DoesNotContain("Shumway Prolog", output);
    }

    [Theory]
    [InlineData("true", 0)]
    [InlineData("fail", 1)]
    [InlineData("throw(boom)", 2)]
    [InlineData("halt(3)", 3)]
    public void TheTopLevelGoalsOutcomeIsTheExitStatus(string goal, int status)
    {
        using var dir = new TempDir();
        Assert.Equal(status, Run(ReplExe, dir.Path, "", out _, "-q", "-t", goal));
    }

    // Saving takes bundles and objects: a source file is refused before anything
    // is built.
    [Fact]
    public void SavingTakesBundlesAndObjects_NotSourceFiles()
    {
        using var dir = new TempDir();
        dir.File("app.pl", "p.\n");
        Assert.Equal(1, Run(ReplExe, dir.Path, "", out string output, "--save-toplevel", "tl", "app.pl"));
        Assert.Contains("app.pl is neither", output);
        Assert.False(File.Exists(Path.Combine(dir.Path, OperatingSystem.IsWindows() ? "tl.exe" : "tl")));
    }

    // End to end: a bundle saved as a top level with a goal and -q, run as
    // shumway runs. Opt-in like the other executable builds (dotnet publish
    // takes tens of seconds): set SHUMWAY_RUN_EXE_TESTS=1.
    [Fact]
    public void ASavedTopLevelLoadsItsBundlesRunsItsGoalsAndReadsItsCommandLine()
    {
        if (Environment.GetEnvironmentVariable("SHUMWAY_RUN_EXE_TESTS") != "1") return;
        using var dir = new TempDir();
        dir.File("app.pl", ":- module(app).\n:- public greet/1.\ngreet(X) :- write(hello(X)), nl.\n"
            + ":- dynamic seen/1.\n");
        Assert.Equal(0, Run(Binary("Shumway.Compile", "shumway-compile"), dir.Path, "", out string c,
            "-o", "app.shmo", "app.pl"));
        Assert.Equal(0, Run(Binary("Shumway.Link", "shumway-link"), dir.Path, "", out string l,
            "--library", "-o", "app.shum", "app.shmo"));
        int saved = Run(ReplExe, dir.Path, "", out string save,
            "--save-toplevel", "tl", "-g", "assertz(seen(saved))", "-q", "app.shum");
        Assert.True(saved == 0, save + c + l);
        string exe = System.IO.Path.Combine(dir.Path, OperatingSystem.IsWindows() ? "tl.exe" : "tl");

        // Interactive: quiet, the bundle loaded, the saved goal run.
        Assert.Equal(0, Run(exe, dir.Path, "seen(X), write(x(X)), nl.\ngreet(world).\nhalt.\n",
            out string session));
        Assert.DoesNotContain("Shumway Prolog", session);
        Assert.Contains("x(saved)", session);
        Assert.Contains("hello(world)", session);

        // Its own command line: a file, a -g goal and a -t goal after the saved ones.
        dir.File("extra.pl", "extra(ok).\n");
        Assert.Equal(1, Run(exe, dir.Path, "", out string run,
            "extra.pl", "-g", "greet(runtime)", "-t", "extra(ok), seen(saved), fail"));
        Assert.Contains("hello(runtime)", run);

        // A saved -t: the executable is a script, and -t on its command line replaces it.
        Assert.Equal(0, Run(ReplExe, dir.Path, "", out string save2,
            "--save-toplevel", "script", "-g", "greet(start)", "-t", "halt", "app.shum"));
        string script = System.IO.Path.Combine(dir.Path, OperatingSystem.IsWindows() ? "script.exe" : "script");
        Assert.Equal(0, Run(script, dir.Path, "", out string ran));
        Assert.Contains("hello(start)", ran);
        Assert.DoesNotContain("Shumway Prolog", ran);
        Assert.Equal(1, Run(script, dir.Path, "", out _, "-t", "fail"));
    }

    // Objects saved around a bundle link into one bundle loaded in the first
    // one's place: each file seeds order/1, so its clauses give the load order.
    // What an object calls in the bundle resolves at run time, and saving warns
    // that the link left it undefined. Opt-in: SHUMWAY_RUN_EXE_TESTS=1.
    [Fact]
    public void ASavedTopLevelLinksItsObjectsIntoOneBundleInTheFirstOnesPlace()
    {
        if (Environment.GetEnvironmentVariable("SHUMWAY_RUN_EXE_TESTS") != "1") return;
        using var dir = new TempDir();
        dir.File("first.pl", @"
:- dynamic order/1.
order(first).
:- public twice/2.
twice(X, Y) :- Y is 2 * X.
");
        dir.File("one.pl", @"
:- dynamic order/1.
order(one).
:- public helper/1.
helper(7).
");
        dir.File("two.pl", @"
:- dynamic order/1.
order(two).
:- public go/1.
go(Y) :- helper(X), twice(X, Y).
");
        Assert.Equal(0, Run(Binary("Shumway.Compile", "shumway-compile"), dir.Path, "", out string c,
            "first.pl", "one.pl", "two.pl"));
        Assert.Equal(0, Run(Binary("Shumway.Link", "shumway-link"), dir.Path, "", out string l,
            "--library", "-o", "first.shum", "first.shmo"));
        int saved = Run(ReplExe, dir.Path, "", out string save,
            "--save-toplevel", "tl", "one.shmo", "first.shum", "two.shmo");
        Assert.True(saved == 0, save + c + l);
        Assert.Contains("warning: Predicate twice/2", save);
        string exe = System.IO.Path.Combine(dir.Path, OperatingSystem.IsWindows() ? "tl.exe" : "tl");
        Assert.Equal(0, Run(exe, dir.Path, "", out string run,
            "-q", "-t", "findall(X, order(X), L), go(Y), write(L-Y), nl"));
        Assert.Contains("[one,two,first]-14", run);
    }
}
