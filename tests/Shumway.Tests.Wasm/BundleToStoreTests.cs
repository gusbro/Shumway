using Shumway.Embedding;
using Shumway.WebHost;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>
/// ADR-076, decisions 4 and 5, in a page: what it keeps of a bundle a
/// collection brings or one it kept. A bundle of another format is built
/// again with the collection's sources and kept; one the page compiled
/// compiles from its source; one that cannot be built again is refused, and
/// its source is not compiled in its place. "Another format" is made here by
/// changing a byte of the stamp.
/// </summary>
public sealed class BundleToStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "shumway_tostore_" + Guid.NewGuid().ToString("N"));

    public BundleToStoreTests() => Directory.CreateDirectory(Path.Combine(_root, "src"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private BundleRebuild.Context Collection => LibraryArchive.RebuildIn(_root, wasmBaker: null);

    // A library of two sources linked with --library --goal, its sources in
    // the collection's src/, as a page imports it.
    private byte[] LinkedWithAGoal()
    {
        string dep = Path.Combine(_root, "src", "gdep.pl");
        File.WriteAllText(dep, ":- module(gdep, [d/1]).\nd(dep).\n");
        string lib = Path.Combine(_root, "src", "glib.pl");
        File.WriteAllText(lib, ":- module(glib, [g/1]).\n:- use_module(gdep).\ng(X) :- d(X).\nhello :- write(goal_ran).\n");
        LinkConfig config = new LinkPlan { Library = true, Goal = "hello", Report = _ => { } }
            .Prepare(new[] { dep, lib }, out _)!;
        Assert.NotNull(config);
        var link = ShmoLinker.Link(config);
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        return link.Bytes!;
    }

    private static byte[] OfAnotherFormat(byte[] image)
    {
        var stale = (byte[])image.Clone();
        stale[8] ^= 0xFF;
        Assert.False(BundleReader.IsCurrent(stale));
        return stale;
    }

    private static string Loaded(byte[] bundle, string query)
    {
        var output = new StringWriter();
        var engine = new PrologEngine { Out = output, Warnings = new StringWriter() };
        engine.LoadBundle(BundleReader.FromBytes(bundle));
        Assert.True(engine.Query(query).Success);
        return output.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void OneOfThisFormat_IsStoredRaw()
    {
        byte[] linked = LinkedWithAGoal();
        var stored = LibraryArchive.ToStore(linked, "glib", imported: true, Collection);
        Assert.Equal(BundleFormat.Uncompressed(linked), stored.Bytes);
        Assert.False(stored.Rebuilt);
        Assert.False(stored.Refused);
    }

    // Imported or kept, a linked library of another format is built again
    // with the collection's sources: the goal its link gave still runs.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALinkedLibraryOfAnotherFormat_IsBuiltAgain_WithItsGoal(bool imported)
    {
        byte[] stale = OfAnotherFormat(LinkedWithAGoal());
        var stored = LibraryArchive.ToStore(stale, "glib", imported, Collection);
        Assert.True(stored.Rebuilt, stored.Reason);
        Assert.True(BundleReader.IsCurrent(stored.Bytes!));
        Assert.Equal("goal_randep\n", Loaded(stored.Bytes!, "glib:g(X), write(X), nl."));
    }

    // Its sources gone, and not carried: refused, naming the source; the
    // library is not compiled from a source in its place.
    [Fact]
    public void ALinkedLibraryWhoseSourcesAreGone_IsRefused_NamingTheSource()
    {
        byte[] stale = OfAnotherFormat(LinkedWithAGoal());
        File.Delete(Path.Combine(_root, "src", "gdep.pl"));
        var stored = LibraryArchive.ToStore(stale, "glib", imported: true, Collection);
        Assert.True(stored.Refused);
        Assert.Null(stored.Bytes);
        Assert.Contains("gdep.pl", stored.Reason);
    }

    // The page's own compile: as today, from its source when it is used.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALibraryThePageCompiled_OfAnotherFormat_CompilesFromItsSource(bool imported)
    {
        byte[] paged = Librarian.CreateArchive(Librarian.ReadArchive(LinkedWithAGoal()), recipe: new BundleRecipe
        {
            Tool = "page",
            Inputs = new[] { new RecipeInput { Kind = RecipeInputKind.Source, Name = "glib.pl" } },
            Consult = true,
        });
        var stored = LibraryArchive.ToStore(OfAnotherFormat(paged), "glib", imported, Collection);
        Assert.Null(stored.Bytes);
        Assert.False(stored.Refused);
        Assert.Contains("another format", stored.Reason);
    }

    // From before bundles said how they were built: brought in, refused; kept
    // by the page, which compiled most of what it kept, compiled from its
    // source. A kept file that is no bundle (a page closed while storing it)
    // is compiled again too; brought in, it is refused.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void OneFromBeforeRecipes_IsRefusedWhenBroughtIn_AndCompiledWhenKept(bool imported, bool refused)
    {
        byte[] old = LinkedWithAGoal();
        old[4] = 6;
        var stored = LibraryArchive.ToStore(old, "glib", imported, Collection);
        Assert.Null(stored.Bytes);
        Assert.Equal(refused, stored.Refused);
        if (refused) Assert.Contains("said how they were built", stored.Reason);

        var none = LibraryArchive.ToStore(Array.Empty<byte>(), "glib", imported, Collection);
        Assert.Null(none.Bytes);
        Assert.Equal(refused, none.Refused);
        if (refused) Assert.Contains("is not a compiled file", none.Reason);
    }
}
