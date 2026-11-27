using System.Diagnostics;
using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>
/// ADR-075, decision 12: <c>shumway-link --library --wasm</c> bakes the
/// library's code as a module that installs in part, its modules calling
/// each other through the table; a link that is not a library bakes one
/// that jumps between them.
/// </summary>
public sealed class LibraryBakeCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway-library-bake-cli-" + Guid.NewGuid().ToString("N"));

    public LibraryBakeCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
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
        proc.WaitForExit(180_000);
        outTask.GetAwaiter().GetResult();
        stderr = errTask.GetAwaiter().GetResult();
        return proc.ExitCode;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALibraryLink_BakesAModuleThatInstallsInPart(bool library)
    {
        File.WriteAllText(Path.Combine(_dir, "a.pl"), @"
:- use_module(library(b)).
ca(X, Y) :- cb(X), Y is X * 3.
");
        File.WriteAllText(Path.Combine(_dir, "b.pl"), @"
:- module(b, [cb/1]).
cb(1).
cb(2).
");
        var args = new List<string> { "--wasm", "--no-compress", "--allow-undefined", "-L", ".", "-o", "x.shum" };
        args.AddRange(library ? new[] { "--library" } : new[] { "--entry", "ca/2" });
        args.AddRange(new[] { "a.pl", "b.pl" });
        Assert.True(Link(out string stderr, args.ToArray()) == 0, stderr);

        var bundle = BundleReader.FromBytes(File.ReadAllBytes(Path.Combine(_dir, "x.shum")));
        var module = WasmRelocatableModule.Read(new MemoryStream(Assert.Single(bundle.WasmModules)));
        Assert.Equal(library, module.InstallsInPart);
    }
}
