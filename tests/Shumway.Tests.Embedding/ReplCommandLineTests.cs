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
    public void PipedInput_GivesTheUnbufferedReadsItsText()
    {
        using var dir = new TempDir();
        // The first read takes the newline that ends the query's line, and the
        // second the next line's first character.
        int code = Run(ReplExe, dir.Path,
            "get_unbuffered_char(C), get_unbuffered_code(D), write(got(C, D)), nl.\nx\nhalt.\n",
            out string output, "-q");
        Assert.Equal(0, code);
        Assert.Contains("got(\n,120)", output.Replace("\r\n", "\n"));
    }

    private static Shumway.Repl.ReplPendingReader PendingWithKey(int key, System.Collections.Generic.List<int> asked, bool terminal = true) =>
        new(() => null, (out int codePoint) =>
        {
            asked.Add(key);
            codePoint = key;
            return terminal;
        });

    [Fact]
    public void AfterAQuery_TheEndOfItsLineDoesNotHideTheKey()
    {
        var asked = new System.Collections.Generic.List<int>();
        var reader = PendingWithKey('k', asked);
        reader.Push("\n");
        Assert.True(reader.TryReadKey(out int key));
        Assert.Equal('k', key);
        Assert.Equal("", reader.Buffered);
    }

    [Fact]
    public void TextTypedAfterAQuery_IsReadBeforeAnyKey()
    {
        var asked = new System.Collections.Generic.List<int>();
        var reader = PendingWithKey('k', asked);
        reader.Push("ab\n");
        Assert.False(reader.TryReadKey(out _));
        Assert.Empty(asked);
        Assert.Equal("ab\n", reader.Buffered);
    }

    [Fact]
    public void WithNoTerminal_TheEndOfTheLineStaysToBeRead()
    {
        var asked = new System.Collections.Generic.List<int>();
        var reader = PendingWithKey('k', asked, terminal: false);
        reader.Push("\n");
        Assert.False(reader.TryReadKey(out _));
        Assert.Equal("\n", reader.Buffered);
    }

    private sealed class FakeConsole
    {
        public readonly System.Collections.Generic.Queue<ConsoleKeyInfo> Keys = new();
        public Action? WhileWaiting;

        public Shumway.Repl.QueryKeyboard Keyboard() => new(() => Keys.Count > 0, () =>
        {
            WhileWaiting?.Invoke();
            return Keys.Dequeue();
        });

        public void Press(string text)
        {
            foreach (char c in text) Keys.Enqueue(Key(c));
        }
    }

    private static ConsoleKeyInfo Key(char c) => c switch
    {
        '\r' => new(c, ConsoleKey.Enter, false, false, false),
        '\b' => new(c, ConsoleKey.Backspace, false, false, false),
        '\u001B' => new(c, ConsoleKey.Escape, false, false, false),
        '\u0003' => new(c, ConsoleKey.C, false, false, true),
        _ => new(c, ConsoleKey.A, false, false, false),
    };

    [Fact]
    public void KeysPressedWhileTheGoalWorks_WaitForItsNextRead()
    {
        var console = new FakeConsole();
        var keyboard = console.Keyboard();
        console.Press("ab");
        Assert.Null(keyboard.Watch());
        Assert.Empty(console.Keys);
        console.Press("c");
        Assert.Equal('a', keyboard.NextKey().KeyChar);
        Assert.Equal('b', keyboard.NextKey().KeyChar);
        Assert.Equal('c', keyboard.NextKey().KeyChar);
    }

    [Theory]
    [InlineData('\u001B')]
    [InlineData('\u0003')]
    public void EscAndCtrlC_GoToTheWatcher_AndTheKeysBeforeThemStay(char special)
    {
        var console = new FakeConsole();
        var keyboard = console.Keyboard();
        console.Press("a" + special);
        Assert.Equal(special, keyboard.Watch()?.KeyChar);
        Assert.Equal('a', keyboard.NextKey().KeyChar);
    }

    [Fact]
    public void WhileAReadWaits_TheWatcherLeavesTheKeysToIt()
    {
        var console = new FakeConsole();
        var keyboard = console.Keyboard();
        console.Press("\u001B");
        ConsoleKeyInfo? seenByWatcher = null;
        int left = -1;
        console.WhileWaiting = () =>
        {
            console.WhileWaiting = null;
            seenByWatcher = keyboard.Watch();
            left = console.Keys.Count;
        };
        Assert.Equal('\u001B', keyboard.NextKey().KeyChar);
        Assert.Null(seenByWatcher);
        Assert.Equal(1, left);

        // The read is over: the watcher takes keys again.
        console.Press("x");
        Assert.Null(keyboard.Watch());
        Assert.Empty(console.Keys);
    }

    [Fact]
    public void ALineTypedAhead_IsComplete_AndTheKeysAfterItStay()
    {
        var console = new FakeConsole();
        var keyboard = console.Keyboard();
        console.Press("xy\rz");
        keyboard.Watch();
        Assert.True(keyboard.BeginLineRead(out string typed));
        Assert.Equal("xy", typed);
        keyboard.EndRead();
        Assert.Equal('z', keyboard.NextKey().KeyChar);
    }

    [Fact]
    public void APartOfALineTypedAhead_IsGiven_WithBackspaceApplied()
    {
        var console = new FakeConsole();
        var keyboard = console.Keyboard();
        console.Press("fox\b");
        console.Keys.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false));
        keyboard.Watch();
        Assert.False(keyboard.BeginLineRead(out string typed));
        Assert.Equal("fo", typed);
    }

    [Fact]
    public void DuringALineRead_TheWatcherLeavesTheKeys()
    {
        var console = new FakeConsole();
        var keyboard = console.Keyboard();
        keyboard.BeginLineRead(out _);
        console.Press("x");
        Assert.Null(keyboard.Watch());
        Assert.Single(console.Keys);
        keyboard.EndRead();
        keyboard.Watch();
        Assert.Empty(console.Keys);
    }

    [Fact]
    public void ClearingDropsTheKeysNoReadTook()
    {
        var console = new FakeConsole();
        var keyboard = console.Keyboard();
        console.Press("ab");
        keyboard.Watch();
        keyboard.Clear();
        console.Press("c");
        Assert.Equal('c', keyboard.NextKey().KeyChar);
    }

    [Fact]
    public void TheKeysKept_HaveALimit()
    {
        var console = new FakeConsole();
        var keyboard = console.Keyboard();
        console.Press("k" + new string('x', Shumway.Repl.QueryKeyboard.MaxKeysAhead));
        keyboard.Watch();
        Assert.Empty(console.Keys);
        console.Press("z");
        Assert.Equal('k', keyboard.NextKey().KeyChar);
        for (int i = 1; i < Shumway.Repl.QueryKeyboard.MaxKeysAhead; i++)
            Assert.Equal('x', keyboard.NextKey().KeyChar);
        Assert.Equal('z', keyboard.NextKey().KeyChar);
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
