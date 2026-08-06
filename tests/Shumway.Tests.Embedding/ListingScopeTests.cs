using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// What <c>listing/0</c> shows. The user's program — not the libraries the
/// engine happens to be built out of.
///
/// <para>An engine booted from a bundle with a baked prelude holds the prelude
/// as PRECOMPILED records rather than manifest clauses, which is a different
/// path through the enumeration. It used to list every prelude local
/// (<c>$prelude$$member3/3: 2 clauses, source stripped</c>) — noise that also
/// said the engine's own innards had no source, which is true and irrelevant.</para>
/// </summary>
public sealed class ListingScopeTests
{
    private static string Listing(PrologEngine e)
    {
        var sink = new StringWriter();
        e.Out = sink;
        Assert.True(e.Query("listing.").Success);
        return sink.ToString();
    }

    [Fact]
    public void ListsTheUsersPredicatesOnly()
    {
        var e = new PrologEngine();
        e.ConsultString("mine(1).\nmine(2).\n");
        string text = Listing(e);

        Assert.Contains("mine(1)", text);
        Assert.DoesNotContain("$prelude", text);
        Assert.DoesNotContain("source stripped", text);
    }

    [Fact]
    public void LibraryInternalsStayOutAfterLoadingOne()
    {
        var e = new PrologEngine();
        e.UseClpfd();
        e.ConsultString("mine(1).\n");
        string text = Listing(e);

        Assert.Contains("mine(1)", text);
        Assert.DoesNotContain("clpfd$", text);
        Assert.DoesNotContain("$prelude", text);
    }

    [Fact]
    public void ABakedPreludeBundleListsNoneOfIt()
    {
        // The path the web app boots on, and the one that reported this: with the
        // prelude baked into the bundle it arrives as precompiled records, not as
        // a module manifest, so filtering by module name alone misses it.
        byte[] bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(":- public mine/1.\nmine(1).\n", "m") },
            EntryPoints = new[] { new PredicateRef("mine", 1) },
            BakePrelude = true,
        }).Bytes!;

        var e = PrologEngine.FromBundle(BundleReader.FromBytes(bytes));
        string text = Listing(e);

        // The user's own predicate is reported — with no source to show, since a
        // release bundle carries none, which is what that message is FOR.
        Assert.Contains("mine/1", text);
        // The prelude's, on the other hand, are not the user's program at all.
        Assert.DoesNotContain("$prelude", text);
    }
}
