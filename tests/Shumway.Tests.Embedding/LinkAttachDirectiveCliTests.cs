using System.Diagnostics;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-075, decision 3, through <c>shumway-link</c>: a source that attaches a
/// library with <c>:- ensure_loaded(bundle(x))</c> links against it as with
/// <c>--dynlib x</c>, compiled file at a time and through the consult.
/// </summary>
public sealed class LinkAttachDirectiveCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_attachcli_" + Guid.NewGuid().ToString("N"));

    public LinkAttachDirectiveCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string LinkExe => LocateBinary("shumway-link", "Shumway.Link");

    private static string LocateBinary(string name, string project)
    {
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string root = LocateRepoRoot();
        string debug = Path.Combine(root, "src", project, "bin", "Debug", "net10.0", name + suffix);
        return File.Exists(debug)
            ? debug
            : Path.Combine(root, "src", project, "bin", "Release", "net10.0", name + suffix);
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

    private (int Exit, string Stderr) Link(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = LinkExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Not the directory of the sources: what the link finds there is
            // what the consult found, not the search path.
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.Environment.Remove("SHUMWAY_LIBRARY_PATH");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);
        return (proc.HasExited ? proc.ExitCode : -1, stderr);
    }

    // The library, and next to it a program that attaches it.
    private string Setup()
    {
        var objects = new[]
        {
            ShmoCompiler.CompileSource("pa(1).\npa(2).\n", "ma"),
            ShmoCompiler.CompileSource(":- module(mq, [q/1]).\nq(X) :- hidden(X).\nhidden(42).\n", "mq"),
            ShmoCompiler.CompileSource("unused(0).\n", "mz"),
        };
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects,
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            AllowUndefined = true,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        File.WriteAllBytes(Path.Combine(_dir, "lib.shum"),
            BundleWriter.ToBytes(link.Bundle!, includeCompiledBytecode: true));
        string prog = Path.Combine(_dir, "prog.pl");
        File.WriteAllText(prog,
            ":- ensure_loaded(bundle(lib)).\nmain(X, Y) :- pa(X), mq:hidden(Y).\n");
        return prog;
    }

    private void RunsAgainstTheLibrary(string bundle)
    {
        var read = BundleReader.ReadFromFile(bundle);
        Assert.Equal("lib:[:pa/1,mq:hidden/1]", string.Join(" ", read.DynamicLibraries.Select(l =>
            l.Name + ":[" + string.Join(",", l.Uses.Select(u => $"{u.Module}:{u.Name}/{u.Arity}")) + "]")));
        var e = new PrologEngine { Out = new StringWriter(), Warnings = new StringWriter() };
        e.LoadBundle(bundle);
        Assert.True(e.IsCodePending("mz"));
        Assert.True(e.Query("main(X, Y), X == 1, Y == 42.").Success);
    }

    [Fact]
    public void ConsultedForTheLink_TheLibraryTheConsultAttached_IsLinkedAgainst()
    {
        string prog = Setup();
        string output = Path.Combine(_dir, "prog.shum");
        var (exit, stderr) = Link("--consult", "-o", output, "--entry", "main/2", prog);
        Assert.True(exit == 0, stderr);
        RunsAgainstTheLibrary(output);
    }

    [Fact]
    public void CompiledFileAtATime_TheLibraryIsFoundOnTheSearchPath()
    {
        string prog = Setup();
        string output = Path.Combine(_dir, "prog.shum");
        var (exit, stderr) = Link("-L", _dir, "-o", output, "--entry", "main/2", prog);
        Assert.True(exit == 0, stderr);
        RunsAgainstTheLibrary(output);

        // Not on the search path, it is not found.
        File.Delete(output);
        (exit, stderr) = Link("-o", output, "--entry", "main/2", prog);
        Assert.NotEqual(0, exit);
        Assert.Contains("library bundle 'lib'", stderr);
    }
}
