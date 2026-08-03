using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 94 (Phase 7): the generated predicate reference. Predicate doc
/// metadata lives next to each definition — a category/summary at the C#
/// builtin registration site, a structured <c>%!</c> comment in the Prolog
/// library sources — and <see cref="PredicateDoc"/> assembles it into
/// <c>docs/guide/predicates.md</c>. The staleness test below keeps that file in
/// step with the code.
/// </summary>
public class Chunk94Tests
{
    /// <summary>Walks up from the test binary to the repository root,
    /// identified by the solution file.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "Shumway.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException(
                "Could not locate the repository root (Shumway.slnx).");
        return dir.FullName;
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n");

    /// <summary>The committed reference must match what the generator
    /// produces. Run the suite with the <c>SHUMWAY_REGEN_DOCS</c>
    /// environment variable set to rewrite it.</summary>
    [Fact]
    public void PredicateReference_IsUpToDate()
    {
        string path = Path.Combine(RepoRoot(), "docs", "guide", "predicates.md");
        string generated = Normalize(PredicateDoc.Generate());

        if (Environment.GetEnvironmentVariable("SHUMWAY_REGEN_DOCS") is not null)
        {
            File.WriteAllText(path, generated);
            return;
        }

        Assert.True(File.Exists(path),
            "docs/guide/predicates.md is missing — regenerate with SHUMWAY_REGEN_DOCS set.");
        Assert.True(generated == Normalize(File.ReadAllText(path)),
            "docs/guide/predicates.md is stale: a predicate's doc metadata changed. " +
            "Regenerate by running the suite with the SHUMWAY_REGEN_DOCS " +
            "environment variable set.");
    }

    [Fact]
    public void Generated_GroupsPredicatesByArea()
    {
        string doc = PredicateDoc.Generate();
        Assert.Contains("# Shumway predicate reference", doc);
        Assert.Contains("## Arithmetic", doc);
        Assert.Contains("## Lists", doc);
        Assert.Contains("## CLP(FD) — labeling", doc);
    }

    [Fact]
    public void Generated_IncludesBuiltinPreludeAndClpfdPredicates()
    {
        string doc = PredicateDoc.Generate();
        Assert.Contains("`is(?Result, +Expr)`", doc);     // C# builtin
        Assert.Contains("`member(?Elem, ?List)`", doc);   // prelude
        Assert.Contains("`label(+Vars)`", doc);           // CLP(FD)
        Assert.Contains("`all_distinct(?Vars)`", doc);
    }

    [Fact]
    public void Generated_ShowsModedCallTemplates()
    {
        string doc = PredicateDoc.Generate();
        // The predicate column is a call template with named, moded
        // parameters rather than a bare name/arity indicator.
        Assert.Contains("`between(+Low, +High, ?X)`", doc);
        Assert.DoesNotContain("`between/3`", doc);
    }

    [Fact]
    public void Generated_OmitsInternalDollarHelpers()
    {
        string doc = PredicateDoc.Generate();
        Assert.DoesNotContain("$findall_push", doc);
        Assert.DoesNotContain("$call", doc);
        Assert.DoesNotContain("$fd_", doc);
    }
}
