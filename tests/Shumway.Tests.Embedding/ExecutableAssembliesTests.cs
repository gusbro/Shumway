using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An executable <c>shumway-link --exe</c> or <c>--save-toplevel</c>
/// builds references the engine's assemblies and what they use, not every
/// Shumway assembly beside the linker: a tool's own (the wasm baker) would be
/// published into every executable without being used.</summary>
public sealed class ExecutableAssembliesTests
{
#if !NETFRAMEWORK
    private static readonly string[] Engine =
    {
        "Shumway.Embedding", "Shumway.Core", "Shumway.Compiler", "Shumway.Interpreter",
        "Shumway.Compiler.Il", "Shumway.Builtins", "Shumway.Libraries",
    };

    [Fact]
    public void AnAssemblyTheEngineDoesNotUseStaysOut()
    {
        using var dir = new LinkerDir();
        // A Shumway assembly beside the linker that nothing in the engine references.
        string toolOnly = dir.Add(typeof(ExecutableAssembliesTests).Assembly.Location, "Shumway.ToolOnly.dll");

        var names = Names(ExecutableEmitter.EnumerateRequiredAssemblies(dir.Path));

        Assert.True(File.Exists(toolOnly));
        foreach (string engine in Engine) Assert.Contains(engine, names);
        Assert.DoesNotContain("Shumway.ToolOnly", names);
        Assert.DoesNotContain("Shumway.TopLevel", names);
    }

    [Fact]
    public void WhatAnExtraReferenceUsesComesAlong_ItselfLeftToTheCaller()
    {
        using var dir = new LinkerDir();
        // The top level's own assembly, which --save-toplevel references: it runs
        // Shumway.TopLevel, which the engine alone does not use.
        string topLevel = dir.Add(typeof(Shumway.Repl.ReplTopLevel).Assembly.Location, "shumway.dll");

        var names = Names(ExecutableEmitter.EnumerateRequiredAssemblies(dir.Path, new[] { topLevel }));

        foreach (string engine in Engine) Assert.Contains(engine, names);
        Assert.Contains("Shumway.TopLevel", names);
        Assert.DoesNotContain("shumway", names);
    }

    [Fact]
    public void AnExecutablesProjectReferencesNoAssemblyTheEngineDoesNotUse()
    {
        string project = ExecutableEmitter.GenerateProjectFile("app", "win-x64",
            ExecutableDeploymentMode.FrameworkDependent, Array.Empty<string>(), Array.Empty<string>());

        Assert.Contains("<Reference Include=\"Shumway.Embedding\">", project);
        Assert.Contains("<Reference Include=\"Shumway.Libraries\">", project);
        // Beside the tests, as beside the toolchain, but unused by the engine.
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Shumway.TopLevel.dll")));
        Assert.DoesNotContain("Shumway.TopLevel", project);
    }

    private static HashSet<string> Names(IEnumerable<string> paths)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in paths) names.Add(Path.GetFileNameWithoutExtension(path));
        return names;
    }

    // A copy of the engine's assemblies, as a toolchain directory holds them.
    private sealed class LinkerDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("shumway-exe-assemblies").FullName;

        public LinkerDir()
        {
            string bin = System.IO.Path.GetDirectoryName(typeof(PrologEngine).Assembly.Location)!;
            foreach (string file in Directory.GetFiles(bin, "Shumway.*.dll"))
                File.Copy(file, System.IO.Path.Combine(Path, System.IO.Path.GetFileName(file)));
        }

        public string Add(string from, string name)
        {
            string to = System.IO.Path.Combine(Path, name);
            File.Copy(from, to, overwrite: true);
            return to;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
#endif
}
