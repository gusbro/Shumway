using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 255: <c>listing</c> for source-stripped <c>.shum</c>
/// bundles. The chunk-254 path walks <c>manifest.Clauses</c> —
/// empty for stripped bundles since <c>LoadEntryFromBytecode</c>
/// populates only the precompiled bytecode + visibility metadata,
/// not the AST clauses. Without the chunk-255 fallback, listing a
/// stripped-bundle predicate prints nothing and returns
/// <c>true.</c> — misleading.
///
/// <para>The fix surfaces a comment line with the predicate's
/// indicator and clause count so the user sees the predicate
/// exists, even though the source isn't available to print.</para>
/// </summary>
public class Chunk255Tests
{
    private static byte[] BuildStrippedBundleBytes(string source,
        params PredicateRef[] entries)
    {
        var obj = ShmoCompiler.CompileSource(source);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = entries,
            StripSource = true,
        });
        Assert.True(result.Success);
        return result.Bytes!;
    }

    private static string CaptureListing(byte[] bundleBytes, string predName)
    {
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bundleBytes));
        var sw = new StringWriter();
        engine.Out = sw;
        engine.Query($"listing({predName}).");
        return sw.ToString();
    }

    [Fact]
    public void StrippedBundle_ListingPred_ShowsCommentWithClauseCount()
    {
        byte[] bytes = BuildStrippedBundleBytes(
            ":- public greet/2.\n"
            + "greet(X, Y) :- Y = hello(X).\n",
            new PredicateRef("greet", 2));
        string output = CaptureListing(bytes, "greet");
        // Comment line: "% greet/2: 1 clause, source stripped ..."
        Assert.Contains("greet/2", output);
        Assert.Contains("1 clause", output);
        Assert.Contains("source stripped", output);
    }

    [Fact]
    public void StrippedBundle_ListingPred_PluralizesCorrectly()
    {
        byte[] bytes = BuildStrippedBundleBytes(
            ":- public sum_of/2.\n"
            + "sum_of([], 0).\n"
            + "sum_of([H|T], Sum) :- sum_of(T, Rest), Sum is H + Rest.\n",
            new PredicateRef("sum_of", 2));
        string output = CaptureListing(bytes, "sum_of");
        Assert.Contains("sum_of/2", output);
        Assert.Contains("2 clauses", output);  // plural
        Assert.Contains("source stripped", output);
    }

    [Fact]
    public void StrippedBundle_ListingZero_EnumeratesAllUserPredicates()
    {
        // listing/0 (no arg) must also see source-stripped predicates.
        byte[] bytes = BuildStrippedBundleBytes(
            ":- public foo/1.\n"
            + ":- public bar/0.\n"
            + "foo(_).\n"
            + "bar.\n",
            new PredicateRef("foo", 1),
            new PredicateRef("bar", 0));

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));
        var sw = new StringWriter();
        engine.Out = sw;
        engine.Query("listing.");
        string output = sw.ToString();

        Assert.Contains("foo/1", output);
        Assert.Contains("bar/0", output);
        Assert.Contains("source stripped", output);
    }

    [Fact]
    public void StrippedBundle_NoSuchPredicate_NoOutput()
    {
        byte[] bytes = BuildStrippedBundleBytes(
            ":- public p/0.\np.\n",
            new PredicateRef("p", 0));
        string output = CaptureListing(bytes, "does_not_exist");
        // No clauses + no precompiled record → nothing printed.
        Assert.DoesNotContain("source stripped", output);
    }

    [Fact]
    public void SourceBearingBundle_StillShowsClausesWithVariableNames()
    {
        // Sanity: a non-stripped bundle takes the chunk-254 path,
        // not the chunk-255 fallback. Variable names from the
        // source must still appear.
        var obj = ShmoCompiler.CompileSource("""
            :- public greet/2.
            greet(X, Y) :- Y = hello(X).
            """,
            buildMode: ShmoBuildMode.Debug);   // debug keeps source
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("greet", 2) },
            StripSource = false,
        });
        Assert.True(result.Success);

        string output = CaptureListing(result.Bytes!, "greet");
        Assert.Contains("greet(X, Y)", output);
        Assert.Contains("Y=hello(X)", output);
        Assert.DoesNotContain("source stripped", output);
    }
}
