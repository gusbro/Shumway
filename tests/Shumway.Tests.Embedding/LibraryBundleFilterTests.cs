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
}
