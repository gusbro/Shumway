using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A host that keeps the bundles it compiles is asked about one as
/// the engine loads it, with the bundle the load has read
/// (<see cref="PrologEngine.LibraryBundleFilter"/>). A bundle it refuses is
/// as none: the library loads from its source, through the host's resolver,
/// which may compile it again.</summary>
public sealed class LibraryBundleFilterTests : IDisposable
{
    // A collection as a host lays one out: its bundles at the root, which is
    // searched first, and its sources under it.
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "shumway_libfilter_" + Guid.NewGuid().ToString("N"));
    private string Sources => Path.Combine(_root, "src");

    public LibraryBundleFilterTests() => Directory.CreateDirectory(Sources);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static byte[] Compiled(string version)
        => Librarian.CreateArchive(new[]
        {
            new BundleArchiveMember("verlib.shmo", ShmoWriter.ToBytes(
                ShmoCompiler.CompileSource(Source(version), "verlib"))),
        });

    private static string Source(string version)
        => $":- module(verlib, [version/1]).\nversion({version}).\n";

    private string Bundle => Path.Combine(_root, "verlib.shum");

    // The bundle says `stored`, the source `source`: which one loaded shows.
    private PrologEngine Engine()
    {
        File.WriteAllBytes(Bundle, Compiled("stored"));
        File.WriteAllText(Path.Combine(Sources, "verlib.pl"), Source("source"));
        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_root);
        e.AddLibraryDirectory(Sources);
        return e;
    }

    private static string Loaded(PrologEngine e)
    {
        e.ConsultString(":- use_module(library(verlib)).");
        var r = e.Query("version(V).");
        Assert.True(r.Success);
        return r.Bindings["V"].ToString()!;
    }

    [Fact]
    public void TheHostIsAskedOnce_WithTheBundleTheLoadRead()
    {
        var e = Engine();
        var asked = new List<(string Name, string Path, int Members)>();
        e.LibraryBundleFilter = (name, path, bundle) =>
        {
            asked.Add((name, path, bundle.ArchiveMembers.Count));
            return true;
        };

        string loaded = Loaded(e);

        Assert.Equal("stored", loaded);
        var (name, path, members) = Assert.Single(asked);
        Assert.Equal("verlib", name);
        Assert.Equal(Path.GetFullPath(Bundle), Path.GetFullPath(path));
        Assert.Equal(1, members);
    }

    [Fact]
    public void ABundleTheHostRefuses_IsAsNone_TheSourceLoads()
    {
        var e = Engine();
        e.LibraryBundleFilter = (_, _, _) => false;

        Assert.Equal("source", Loaded(e));
    }

    /// <summary>What a host that compiles on demand does: the refused bundle
    /// goes, and its resolver answers with the one it compiles in its place,
    /// which is loaded without asking again.</summary>
    [Fact]
    public void TheResolverAnswersWithTheBundleItCompiledAgain_AndThatLoads()
    {
        var e = Engine();
        int asked = 0, resolved = 0;
        e.LibraryBundleFilter = (_, path, _) =>
        {
            asked++;
            File.Delete(path);
            return false;
        };
        e.LibrarySourceResolver = (name, source) =>
        {
            resolved++;
            Assert.Equal("verlib", name);
            Assert.Equal(Path.GetFullPath(Path.Combine(Sources, "verlib.pl")), Path.GetFullPath(source));
            File.WriteAllBytes(Bundle, Compiled("compiled_again"));
            return Bundle;
        };

        string loaded = Loaded(e);

        Assert.Equal("compiled_again", loaded);
        Assert.Equal(1, asked);
        Assert.Equal(1, resolved);
    }

    /// <summary>Counter-proof: with no filter the bundle loads as it always did.</summary>
    [Fact]
    public void WithNoFilter_TheBundleLoads()
    {
        Assert.Equal("stored", Loaded(Engine()));
    }

    // The library's bundle, as an engine with another format of compiled
    // code wrote it: the stamp after its version is another. Compiled by a
    // page from its source, unless a host wrote it (ADR-076).
    private void WriteABundleOfAnotherFormat(bool byAPage = true)
    {
        byte[] stale = byAPage
            ? Librarian.CreateArchive(Librarian.ReadArchive(Compiled("stale")), recipe: new BundleRecipe
            {
                Tool = "page",
                Inputs = new[] { new RecipeInput { Kind = RecipeInputKind.Source, Name = "verlib.pl" } },
                Consult = true,
            })
            : Compiled("stale");
        stale[8] ^= 0xFF;
        File.WriteAllBytes(Bundle, stale);
    }

    /// <summary>A library a page compiled, of another format, is as none when
    /// its source is there: the source loads, with a warning, and the host is
    /// not asked about a bundle the engine cannot read.</summary>
    [Fact]
    public void ABundleOfAnotherFormat_IsAsNone_TheSourceLoads()
    {
        var e = Engine();
        var warnings = new StringWriter();
        e.Warnings = warnings;
        WriteABundleOfAnotherFormat();
        int asked = 0;
        e.LibraryBundleFilter = (_, _, _) => { asked++; return true; };

        string loaded = Loaded(e);

        Assert.Equal("source", loaded);
        Assert.Equal(0, asked);
        Assert.Contains("library(verlib): its bundle was written with another format", warnings.ToString());
    }

    /// <summary>A host that compiles on demand is asked for the library's
    /// source, and the bundle it compiles in the stale one's place loads.</summary>
    [Fact]
    public void ABundleOfAnotherFormat_IsCompiledAgainByAHostThatDoes()
    {
        var e = Engine();
        WriteABundleOfAnotherFormat();
        int resolved = 0;
        e.LibrarySourceResolver = (_, _) =>
        {
            resolved++;
            File.WriteAllBytes(Bundle, Compiled("compiled_again"));
            return Bundle;
        };

        string loaded = Loaded(e);

        Assert.Equal("compiled_again", loaded);
        Assert.Equal(1, resolved);
    }

    /// <summary>With no source to load in its place the load fails, and says
    /// what to do about the bundle.</summary>
    [Fact]
    public void ABundleOfAnotherFormatWithNoSource_FailsSayingSo()
    {
        var e = Engine();
        var warnings = new StringWriter();
        e.Warnings = warnings;
        WriteABundleOfAnotherFormat();
        File.Delete(Path.Combine(Sources, "verlib.pl"));

        e.ConsultString(":- use_module(library(verlib)).");

        Assert.Contains("the page compiles it again from its source", warnings.ToString());
        Assert.False(e.Query("catch(version(_), _, fail).").Success);
    }

    /// <summary>A bundle a host wrote says nothing of how it was built: of
    /// another format, it is refused, and the source beside it is not loaded
    /// in its place (ADR-076, decision 5).</summary>
    [Fact]
    public void AHostsBundleOfAnotherFormat_IsRefused_AndTheSourceIsNotLoaded()
    {
        var e = Engine();
        var warnings = new StringWriter();
        e.Warnings = warnings;
        WriteABundleOfAnotherFormat(byAPage: false);

        e.ConsultString(":- use_module(library(verlib)).");

        Assert.Contains("written by a host", warnings.ToString());
        Assert.False(e.Query("catch(version(_), _, fail).").Success);
    }
}
