using System.Diagnostics;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-076, decision 3, through the tools: <c>shumway-compile</c>,
/// <c>shumway-link</c> and <c>shumway-lib</c> each write the recipe of what
/// they built into it.
/// </summary>
public sealed class RecipeCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_recipe_cli_" + Guid.NewGuid().ToString("N"));

    public RecipeCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Tool(string project, string name)
    {
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string current = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(current, "Shumway.slnx")))
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException("Could not locate repository root.");
        string bin = Path.Combine(current, "src", project, "bin");
        string debug = Path.Combine(bin, "Debug", "net10.0", name + suffix);
        return File.Exists(debug) ? debug : Path.Combine(bin, "Release", "net10.0", name + suffix);
    }

    private void Run(string project, string name, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Tool(project, name),
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
        string stderr = errTask.GetAwaiter().GetResult();
        Assert.True(proc.ExitCode == 0, $"{name} {string.Join(" ", args)}: {stderr}");
    }

    private void Write(string name, string source) => File.WriteAllText(Path.Combine(_dir, name), source);

    private BundleRecipe RecipeOf(string name)
        => BundleFormat.HeaderRecipe(File.ReadAllBytes(Path.Combine(_dir, name)))!.Value.Recipe!;

    [Fact]
    public void Compile_RecordsTheSourceTheModuleAndTheMode()
    {
        Write("a.pl", @"
:- module(a, [p/0]).
p.
");
        Run("Shumway.Compile", "shumway-compile", "--debug", "-o", "a.shmo", "a.pl");
        BundleRecipe recipe = RecipeOf("a.shmo");
        Assert.Equal("shumway-compile", recipe.Tool);
        Assert.Equal("a", recipe.Module);
        Assert.Equal("a.pl", Assert.Single(recipe.Inputs).Name);
        Assert.Equal("a.pl", Assert.Single(recipe.Sources).Name);
        Assert.True(recipe.Debug);
        Assert.False(recipe.Consult);
    }

    // With --with-source the object's recipe carries the file as it is.
    [Fact]
    public void CompileWithSource_CarriesTheFile()
    {
        Write("a.pl", @"
:- module(a, [p/0]).
p.
");
        Run("Shumway.Compile", "shumway-compile", "--with-source", "-o", "a.shmo", "a.pl");
        RecipeSource source = Assert.Single(RecipeOf("a.shmo").Sources);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "a.pl")), source.Contents);

        Run("Shumway.Compile", "shumway-compile", "-o", "b.shmo", "a.pl");
        Assert.Null(Assert.Single(RecipeOf("b.shmo").Sources).Contents);
    }

    // A compile by consult writes one object for each module the roots
    // brought in; each says so, and which module it is.
    [Fact]
    public void CompileByConsult_EachObjectNamesTheRootsAndItsModule()
    {
        Write("dep.pl", @"
:- module(dep, [d/0]).
d.
");
        Write("root.pl", @"
:- module(root, [r/0]).
:- use_module(dep).
r :- d.
");
        Run("Shumway.Compile", "shumway-compile", "--consult", "-o", ".", "root.pl");
        foreach (string module in new[] { "root", "dep" })
        {
            BundleRecipe recipe = RecipeOf(module + ".shmo");
            Assert.True(recipe.Consult);
            Assert.Equal(module, recipe.Module);
            Assert.Equal("root.pl", Assert.Single(recipe.Inputs).Name);
        }
    }

    [Fact]
    public void Link_RecordsItsInputsAndOptions()
    {
        Write("a.pl", @"
greet :- write(hello), nl.
");
        Write("b.pl", @"
:- module(b, [q/0]).
q.
");
        Run("Shumway.Compile", "shumway-compile", "-o", "b.shmo", "b.pl");
        Run("Shumway.Link", "shumway-link", "--library", "--goal", "greet", "--allow-undefined",
            "--strip", "-o", "x.shum", "a.pl", "b.shmo");
        BundleRecipe recipe = RecipeOf("x.shum");
        Assert.Equal("shumway-link", recipe.Tool);
        Assert.Equal(new[] { (RecipeInputKind.Source, "a.pl"), (RecipeInputKind.Object, "b.shmo") },
            recipe.Inputs.Select(i => (i.Kind, i.Name)));
        Assert.Equal("shumway-compile", recipe.Inputs[1].Recipe!.Tool);
        Assert.Equal("a.pl", Assert.Single(recipe.Sources).Name);
        Assert.True(recipe.Library && recipe.AllowUndefined && recipe.Strip);
        Assert.Equal("greet", recipe.Goal);
    }

    [Fact]
    public void Lib_RecordsItsMembers()
    {
        Write("m1.pl", @"
:- module(m1, [p/0]).
p.
");
        Run("Shumway.Compile", "shumway-compile", "-o", "m1.shmo", "m1.pl");
        Run("Shumway.Lib", "shumway-lib", "create", "lib.shum", "m1.shmo");
        BundleRecipe recipe = RecipeOf("lib.shum");
        Assert.Equal("shumway-lib", recipe.Tool);
        RecipeInput member = Assert.Single(recipe.Inputs);
        Assert.Equal("m1.shmo", member.Name);
        Assert.Equal("m1", member.Recipe!.Module);
    }
}
