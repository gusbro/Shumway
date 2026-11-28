using System.Diagnostics;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <c>shumway-link --library</c>: no <c>--entry</c>, and <c>--goal</c> is the
/// goal the bundle runs when it loads, in the module of the last input.
/// </summary>
public sealed class LibraryGoalCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_library_goal_cli_" + Guid.NewGuid().ToString("N"));

    public LibraryGoalCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string LinkExe
    {
        get
        {
            string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
            string current = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(current, "Shumway.slnx")))
                current = Path.GetDirectoryName(current)
                    ?? throw new InvalidOperationException("Could not locate repository root.");
            string bin = Path.Combine(current, "src", "Shumway.Link", "bin");
            string debug = Path.Combine(bin, "Debug", "net10.0", "shumway-link" + suffix);
            return File.Exists(debug) ? debug : Path.Combine(bin, "Release", "net10.0", "shumway-link" + suffix);
        }
    }

    private int Link(out string stderr, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = LinkExe,
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit(120_000);
        outTask.GetAwaiter().GetResult();
        stderr = errTask.GetAwaiter().GetResult();
        return proc.ExitCode;
    }

    private void Write(string name, string source) => File.WriteAllText(Path.Combine(_dir, name), source);

    private string Loaded(string bundle)
    {
        var output = new StringWriter();
        new PrologEngine { Out = output, Warnings = new StringWriter() }.LoadBundle(Path.Combine(_dir, bundle));
        return output.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void AnEntry_IsRefusedWithALibrary()
    {
        Write("a.pl", "who :- write(a), nl.\n");
        Assert.Equal(3, Link(out string stderr, "--library", "--entry", "who/0", "-o", "x.shum", "a.pl"));
        Assert.Contains("--entry does not go with --library", stderr);
    }

    // The goal is no entry point: it may call a private of its module.
    [Fact]
    public void TheGoal_CallsAPrivateOfItsModule()
    {
        Write("m.pl", @"
:- module(m, [p/0]).
p :- q.
q :- write(q_ran), nl.
");
        Assert.True(Link(out string stderr, "--library", "--goal", "q", "-o", "m.shum", "m.pl") == 0, stderr);
        Assert.Equal("q_ran\n", Loaded("m.shum"));
    }

    // The goal runs in the module of the last input: a source read through
    // the consult, a source compiled alone, or an object.
    [Theory]
    [InlineData("consult", "a")]
    [InlineData("consult", "b")]
    [InlineData("alone", "a")]
    [InlineData("alone", "b")]
    [InlineData("object", "b")]
    public void TheGoal_RunsInTheModuleOfTheLastInput(string inputs, string last)
    {
        // Each module has its own private who/0: a library offers a name from
        // one module only (ADR-075).
        Write("a.pl", ModuleWho("a"));
        Write("b.pl", ModuleWho("b"));
        string first = last == "a" ? "b" : "a";
        var args = new List<string> { "--library", "--goal", "who", "-o", "x.shum" };
        if (inputs == "consult") args.Add("--consult");
        if (inputs == "object")
        {
            ShmoWriter.WriteToFile(ShmoCompiler.CompileSource(ModuleWho("b"), "b",
                ShmoBuildMode.Release), Path.Combine(_dir, "b.shmo"));
            args.AddRange(new[] { "a.pl", "b.shmo" });
        }
        else
            args.AddRange(new[] { first + ".pl", last + ".pl" });
        Assert.True(Link(out string stderr, args.ToArray()) == 0, stderr);
        Assert.Equal(last + "\n", Loaded("x.shum"));
    }

    private static string ModuleWho(string module) => $@"
:- module({module}, []).
who :- write({module}), nl.
";
}
