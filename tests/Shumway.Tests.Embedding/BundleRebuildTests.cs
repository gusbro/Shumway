using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-076, decisions 4 and 5: a file of another format is built again from
/// its recipe, with this engine's compiler and linker and nothing written;
/// one that cannot be is refused with the reason. "Another format" is made
/// here by changing a byte of the stamp.
/// </summary>
public sealed class BundleRebuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_rebuild_" + Guid.NewGuid().ToString("N"));

    public BundleRebuildTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string source)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, source);
        return path;
    }

    private static byte[] OfAnotherFormat(byte[] image)
    {
        var stale = (byte[])image.Clone();
        stale[8] ^= 0xFF;
        Assert.ThrowsAny<Exception>(() => BundleReader.FromBytes(stale));
        return stale;
    }

    private BundleRebuild.Context Beside(List<string>? warnings = null) => new()
    {
        BaseDirectory = _dir,
        FindFile = name => File.Exists(Path.Combine(_dir, name)) ? Path.Combine(_dir, name) : null,
        Warn = w => warnings?.Add(w),
    };

    private static byte[] Link(LinkPlan plan, params string[] inputs)
    {
        LinkConfig config = plan.Prepare(inputs, out _)!;
        Assert.NotNull(config);
        var link = ShmoLinker.Link(config);
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        return link.Bytes!;
    }

    private static string Loaded(byte[] bundle, string? query = null)
    {
        var output = new StringWriter();
        var engine = new PrologEngine { Out = output, Warnings = new StringWriter() };
        engine.LoadBundle(BundleReader.FromBytes(bundle));
        if (query is not null) Assert.True(engine.Query(query).Success);
        return output.ToString().Replace("\r\n", "\n");
    }

    // Five inputs, each saying when it is initialized, and the library's goal
    // in the last: rebuilt, the five in their order and the goal once, in
    // the module of the last.
    [Fact]
    public void ALibraryOfFiveSources_IsRebuilt_InOrder_WithItsGoalOnce()
    {
        var inputs = "abcde".Select(m => Write($"{m}.pl", $@"
:- module({m}, []).
:- initialization(write({m})).
who :- write(goal_in_{m}).
")).ToArray();
        byte[] linked = Link(new LinkPlan { Library = true, Goal = "who", Report = _ => { } }, inputs);
        Assert.Equal("abcdegoal_in_e", Loaded(linked));

        byte[] rebuilt = BundleRebuild.Rebuild(OfAnotherFormat(linked), "lib.shum", Beside());
        Assert.True(BundleReader.IsCurrent(rebuilt));
        Assert.Equal("abcdegoal_in_e", Loaded(rebuilt));
        Assert.Equal("who", BundleReader.FromBytes(rebuilt).Recipe!.Goal);
    }

    private static byte[] ObjectCarrying(string module, string text, DateTime written)
        => ShmoWriter.ToBytes(ShmoCompiler.CompileSource(text, module).WithRecipe(new BundleRecipe
        {
            Tool = "shumway-compile",
            Module = module,
            Inputs = new[] { new RecipeInput { Kind = RecipeInputKind.Source, Name = module + ".pl" } },
            Sources = new[] { RecipeSource.Of(module + ".pl", System.Text.Encoding.UTF8.GetBytes(text), written, carry: true) },
            WithSource = true,
        }));

    private const string Carried = @"
:- module(m, [p/1]).
p(carried).
";

    // Built from an object whose recipe carries its source: nothing else is
    // around, and the rebuild compiles the copy.
    [Fact]
    public void ALinkOfACarryingObject_IsRebuiltFromTheCopy()
    {
        File.WriteAllBytes(Path.Combine(_dir, "m.shmo"),
            ObjectCarrying("m", Carried, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
        byte[] linked = Link(new LinkPlan { EntryPoints = new[] { new PredicateRef("p", 1) }, Report = _ => { } },
            Path.Combine(_dir, "m.shmo"));
        File.Delete(Path.Combine(_dir, "m.shmo"));

        byte[] rebuilt = BundleRebuild.Rebuild(OfAnotherFormat(linked), "app.shum", Beside());
        Assert.Equal("carried\n", Loaded(rebuilt, "m:p(X), write(X), nl."));
    }

    // The file beside is newer than the copy and differs: it is what the
    // rebuild compiles, and the rebuild says so. The same file says nothing.
    [Fact]
    public void ASourceEditedSinceTheBuild_IsCompiled_AndTheRebuildSaysSo()
    {
        var recorded = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllBytes(Path.Combine(_dir, "m.shmo"), ObjectCarrying("m", Carried, recorded));
        byte[] linked = Link(new LinkPlan { EntryPoints = new[] { new PredicateRef("p", 1) }, Report = _ => { } },
            Path.Combine(_dir, "m.shmo"));
        File.Delete(Path.Combine(_dir, "m.shmo"));

        string edited = Write("m.pl", Carried.Replace("p(carried)", "p(edited)"));
        File.SetLastWriteTimeUtc(edited, recorded.AddDays(1));
        var warnings = new List<string>();
        byte[] rebuilt = BundleRebuild.Rebuild(OfAnotherFormat(linked), "app.shum", Beside(warnings));
        Assert.Equal("edited\n", Loaded(rebuilt, "m:p(X), write(X), nl."));
        Assert.Contains(warnings, w => w.Contains("m.pl changed since m.shmo was built"));

        File.WriteAllText(edited, Carried);
        warnings.Clear();
        BundleRebuild.Rebuild(OfAnotherFormat(linked), "app.shum", Beside(warnings));
        Assert.Empty(warnings);
    }

    // A link by consult: the rebuild consults its root again, and the module
    // the root brought in comes with it.
    [Fact]
    public void ALinkByConsult_IsRebuiltByConsult()
    {
        Write("dep.pl", @"
:- module(dep, [d/1]).
d(from_dep).
");
        string root = Write("root.pl", @"
:- module(root, [r/1]).
:- use_module(dep).
r(X) :- d(X).
");
        byte[] linked = Link(new LinkPlan
        {
            Consult = true,
            EntryPoints = new[] { new PredicateRef("r", 1) },
            LibraryDirs = new[] { _dir },
            Report = _ => { },
        }, root);
        Assert.True(BundleReader.FromBytes(linked).Recipe!.Consult);

        byte[] rebuilt = BundleRebuild.Rebuild(OfAnotherFormat(linked), "app.shum", Beside());
        Assert.Equal("from_dep\n", Loaded(rebuilt, "root:r(X), write(X), nl."));
    }

    // use_module of a library whose bundle is of another format: rebuilt in
    // memory from the source beside it, loaded, said; the file on disk is
    // left as it was.
    [Fact]
    public void UseModule_RebuildsALibraryOfAnotherFormat_InMemory()
    {
        string lib = Write("vlib.pl", @"
:- module(vlib, [v/1]).
v(rebuilt).
");
        byte[] linked = Link(new LinkPlan { Library = true, Report = _ => { } }, lib);
        string bundle = Path.Combine(_dir, "vlib.shum");
        byte[] stale = OfAnotherFormat(linked);
        File.WriteAllBytes(bundle, stale);
        // Newer than the source: library(vlib) takes the bundle.
        File.SetLastWriteTimeUtc(bundle, File.GetLastWriteTimeUtc(lib).AddMinutes(1));

        var warnings = new StringWriter();
        var output = new StringWriter();
        var engine = new PrologEngine { Out = output, Warnings = warnings };
        engine.AddLibraryDirectory(_dir);
        engine.ConsultString(":- use_module(library(vlib)).");
        Assert.True(engine.Query("v(X), write(X), nl.").Success);
        Assert.Equal("rebuilt\n", output.ToString().Replace("\r\n", "\n"));
        Assert.Contains("library(vlib): its bundle was written with another format of compiled code; it was rebuilt",
            warnings.ToString());
        Assert.Equal(stale, File.ReadAllBytes(bundle));
    }

    // An object of another format given to a link: rebuilt in memory from
    // its source beside it, linked, said.
    [Fact]
    public void ALink_RebuildsAnObjectOfAnotherFormat()
    {
        string source = Write("o.pl", @"
:- module(o, [w/1]).
w(from_object).
");
        var obj = ShmoCompiler.TryCompileFile(source).Object!.WithRecipe(new BundleRecipe
        {
            Tool = "shumway-compile",
            Module = "o",
            Inputs = new[] { new RecipeInput { Kind = RecipeInputKind.Source, Name = "o.pl" } },
            Sources = new[] { RecipeSource.Of("o.pl", File.ReadAllBytes(source), File.GetLastWriteTimeUtc(source)) },
        });
        string shmo = Path.Combine(_dir, "o.shmo");
        File.WriteAllBytes(shmo, OfAnotherFormat(ShmoWriter.ToBytes(obj)));

        var said = new List<string>();
        byte[] linked = Link(new LinkPlan { EntryPoints = new[] { new PredicateRef("w", 1) }, Report = said.Add }, shmo);
        Assert.Contains(said, line => line.Contains("o.shmo is in another format of compiled code: rebuilt"));
        Assert.Equal("from_object\n", Loaded(linked, "o:w(X), write(X), nl."));
    }

    [Fact]
    public void AFileThatCannotBeRebuilt_IsRefused_WithTheReason()
    {
        string a = Write("a.pl", "p.\n");
        byte[] linked = Link(new LinkPlan { EntryPoints = new[] { new PredicateRef("p", 0) }, Report = _ => { } }, a);

        // A source the recipe names, not there, and not carried.
        File.Delete(a);
        var missing = Assert.Throws<RebuildRefusedException>(
            () => BundleRebuild.Rebuild(OfAnotherFormat(linked), "app.shum", Beside()));
        Assert.Contains("built from a.pl, which is not here", missing.Message);

        // A rules version this engine does not rebuild.
        byte[] later = OfAnotherFormat(linked);
        BitConverter.GetBytes(99u).CopyTo(later, 16);
        Assert.Contains("rebuild rules 99",
            Assert.Throws<RebuildRefusedException>(() => BundleRebuild.Rebuild(later, "app.shum", Beside())).Message);

        // A file of the version before the recipe.
        byte[] before = OfAnotherFormat(linked);
        BitConverter.GetBytes(6u).CopyTo(before, 4);
        Assert.Contains("built before compiled files said how they were built",
            Assert.Throws<RebuildRefusedException>(() => BundleRebuild.Rebuild(before, "app.shum", Beside())).Message);

        // A bundle a host wrote, and a saved state.
        byte[] host = BundleWriter.ToBytes(new Bundle(new[] { new BundleEntry("h", "p.\n") }));
        Assert.Contains("written by a host",
            Assert.Throws<RebuildRefusedException>(() => BundleRebuild.Rebuild(OfAnotherFormat(host), "h.shum", Beside())).Message);
        var engine = new PrologEngine();
        engine.ConsultString("q(1).\n");
        string statePath = Path.Combine(_dir, "s.shum");
        engine.SaveState(statePath, false);
        byte[] state = File.ReadAllBytes(statePath);
        Assert.Contains("a saved state",
            Assert.Throws<RebuildRefusedException>(() => BundleRebuild.Rebuild(OfAnotherFormat(state), "s.shum", Beside())).Message);
    }
}
