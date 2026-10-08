using System.Diagnostics;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>shumway-compile --consult loads what the program imports, and the
/// engine's own libraries (clpfd, coroutining, the atts a Scryer library
/// pulls in) live in the Shumway.Libraries assembly. The tool did not
/// reference it, so a consult that imported one failed with "Shumway.Libraries
/// could not be loaded", and the program then did not parse without the
/// library's operators. Run as the built executable: in this test process the
/// assembly is always there.</summary>
public sealed class CompileConsultEngineLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_compile_lib_" + Guid.NewGuid().ToString("N"));

    public CompileConsultEngineLibraryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string CompileExe()
    {
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Shumway.slnx")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        foreach (string config in new[] { "Debug", "Release" })
        {
            string path = Path.Combine(root, "src", "Shumway.Compile", "bin", config,
                "net10.0", "shumway-compile" + suffix);
            if (File.Exists(path)) return path;
        }
        throw new InvalidOperationException("shumway-compile is not built");
    }

    [Fact]
    public async Task AConsultThatImportsAnEngineLibraryCompiles()
    {
        File.WriteAllText(Path.Combine(_dir, "app.pl"), """
            :- use_module(library(clpfd)).
            small(X) :- X in 1..3, X #> 1.
            """);
        string objs = Path.Combine(_dir, "objs");
        var psi = new ProcessStartInfo(CompileExe())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _dir,
        };
        foreach (var a in new[] { "--consult", "-o", objs, "app.pl" }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await proc.WaitForExitAsync(timeout.Token);
        string output = await stdout + await stderr;

        Assert.DoesNotContain("could not be loaded", output);
        Assert.Equal(0, proc.ExitCode);
        Assert.True(File.Exists(Path.Combine(objs, "app.shmo")), output);
    }
}
