using System.Text;
using System.Text.RegularExpressions;
using Shumway.Builtins;

namespace Shumway.Embedding;

/// <summary>
/// Generates the user-facing predicate reference (<c>docs/guide/predicates.md</c>)
/// from the predicate definitions themselves, so it never drifts.
///
/// <para>Two metadata sources, both living next to the definition. Each
/// carries a category, a moded call template (e.g. <c>between(+Low, +High,
/// ?X)</c>) and a one-line summary:</para>
/// <list type="bullet">
/// <item>C# builtins pass the three to <see cref="BuiltinsRegistry.Register"/>;
/// internal <c>$</c>-named helpers leave them null and are skipped.</item>
/// <item>Prolog library predicates (the prelude and CLP(FD)) carry a
/// structured <c>%! Template | Category | Summary</c> comment in their
/// source.</item>
/// </list>
///
/// <para>The template's parameter modes follow the usual convention:
/// <c>+</c> bound at call, <c>-</c> output, <c>?</c> either, <c>@</c> not
/// modified, <c>:</c> a meta-called goal.</para>
///
/// <para>A unit test regenerates the document and fails if the committed copy
/// is stale; running the suite with the <c>SHUMWAY_REGEN_DOCS</c> environment
/// variable set rewrites it. As predicates are added or changed the reference
/// is regenerated, never hand-edited.</para>
/// </summary>
public static class PredicateDoc
{
    private sealed record Entry(
        string Category, string Name, int Arity, string Template, string Summary);

    /// <summary>One documented predicate: what it is called, how it is called
    /// (a template naming each parameter and its mode) and what it does.</summary>
    public sealed record DocEntry(
        string Category, string Name, int Arity, string Template, string Summary);

    /// <summary>Every documented predicate, in the order the reference presents
    /// them. The same metadata <see cref="Generate"/> renders as markdown, for a
    /// host that wants to show it its own way — the browser app builds a
    /// searchable reference out of this.</summary>
    public static IReadOnlyList<DocEntry> Entries()
    {
        var entries = Collect();
        var order = OrderedCategories(entries).ToList();
        return entries
            .OrderBy(e => order.IndexOf(e.Category))
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ThenBy(e => e.Arity)
            .Select(e => new DocEntry(e.Category, e.Name, e.Arity, e.Template, e.Summary))
            .ToList();
    }

    /// <summary>Matches a <c>%! Template | Category | Summary</c> comment.</summary>
    private static readonly Regex DocComment = new(
        @"^\s*%!\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*(.+?)\s*$",
        RegexOptions.Compiled);

    /// <summary>The complete section order — every category a predicate
    /// declares is expected to be listed here. One not listed still renders
    /// (after these, alphabetically) so a host's custom category cannot break
    /// doc generation, but for the shipped reference that fallback is drift: a
    /// new name that near-duplicates an existing section ("Atoms" beside
    /// "Atoms &amp; strings"). The doc tests check every emitted category
    /// against this list, which is what catches a stray.</summary>
    private static readonly string[] CategoryOrder =
    {
        "Unification & comparison",
        "Type checking",
        "Arithmetic",
        "Term ordering",
        "Term inspection & construction",
        "Control",
        "Findall & aggregation",
        "Database",
        "Lists",
        "Atoms & strings",
        "Attributed variables",
        "Coroutining",
        "Input / output",
        "Flags, operators & reflection",
        "Grammar",
        "Global variables",
        "Messages",
        "Time",
        "CLP(FD): domains",
        "CLP(FD): arithmetic constraints",
        "CLP(FD): global constraints",
        "CLP(FD): labeling",
        "CLP(FD): reification",
        "CLP(R)",
    };

    /// <summary>The declared section order, for the doc tests' stray-category
    /// guard.</summary>
    internal static IReadOnlyList<string> DeclaredCategoryOrder => CategoryOrder;

    /// <summary>Sections whose predicates come from a library that is not
    /// loaded by default, and how to load it. A reader who lands on
    /// <c>#=/2</c> from a search needs to know why it does not exist yet, and
    /// the answer they need is the Prolog directive: the embedding method is
    /// the same door from the other side, not the main one.</summary>
    private static readonly (string Category, string Library, string Method)[] CategoryLibrary =
    {
        ("Coroutining", "coroutining", "UseCoroutining"),
        ("CLP(FD): domains", "clpfd", "UseClpfd"),
        ("CLP(FD): arithmetic constraints", "clpfd", "UseClpfd"),
        ("CLP(FD): global constraints", "clpfd", "UseClpfd"),
        ("CLP(FD): labeling", "clpfd", "UseClpfd"),
        ("CLP(FD): reification", "clpfd", "UseClpfd"),
        ("CLP(R)", "clpr", "UseClpr"),
    };

    /// <summary>Builds the predicate-reference markdown. Newlines are
    /// always <c>\n</c> so the result is comparable across platforms.</summary>
    public static string Generate() => Render(Collect());

    /// <summary>The documented predicates, as found: C# builtins carry their
    /// metadata on registration, library predicates carry it in a
    /// <c>%! Template | Category | Summary</c> comment next to the clause.</summary>
    private static List<Entry> Collect()
    {
        StandardBuiltins.EnsureRegistered();
        MetaBuiltins.EnsureRegistered();

        var entries = new List<Entry>();
        foreach (var b in BuiltinsRegistry.AllEntries())
            if (b.Category is not null && b.Template is not null &&
                b.Summary is not null && !b.Name.StartsWith('$'))
                entries.Add(new Entry(b.Category, b.Name, b.Arity, b.Template, b.Summary));
        CollectDocComments(Prelude.Source, entries);
        CollectDocComments(Clpfd.Source, entries);
        CollectDocComments(Clpr.Source, entries);
        CollectDocComments(Coroutining.Source, entries);
        return entries;
    }

    private static void CollectDocComments(string source, List<Entry> into)
    {
        foreach (string line in source.Split('\n'))
        {
            Match m = DocComment.Match(line);
            if (!m.Success) continue;
            string template = m.Groups[1].Value.Trim();
            (string name, int arity) = ParseTemplate(template);
            into.Add(new Entry(
                m.Groups[2].Value.Trim(), name, arity, template,
                m.Groups[3].Value.Trim()));
        }
    }

    /// <summary>Derives the name and arity from a call template:
    /// <c>append(?A, ?B, ?C)</c> is <c>append/3</c>, <c>nl</c> is
    /// <c>nl/0</c>.</summary>
    private static (string Name, int Arity) ParseTemplate(string template)
    {
        int open = template.IndexOf('(');
        if (open < 0) return (template, 0);
        string name = template[..open].Trim();
        int close = template.LastIndexOf(')');
        string inner = template.Substring(open + 1, close - open - 1).Trim();
        return (name, inner.Length == 0 ? 0 : inner.Split(',').Length);
    }

    private static string Render(List<Entry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("# Shumway predicate reference\n\n");
        sb.Append("_Generated by `Shumway.Embedding.PredicateDoc`. Do not edit by hand._\n");
        sb.Append("_Regenerate by running the test suite with the `SHUMWAY_REGEN_DOCS` ");
        sb.Append("environment variable set._\n\n");
        sb.Append("Every predicate Shumway provides, grouped by area. Most are ");
        sb.Append("available to any program; a section whose predicates come from a ");
        sb.Append("library says under its heading which one to load.\n\n");
        sb.Append("Each template names its parameters and their mode: `+` bound at call, ");
        sb.Append("`-` an output, `?` either, `@` not modified, `:` a meta-called goal.\n");

        // Contents — this is a reference people land in looking for ONE
        // predicate; a section row beats scrolling 27 headings. Anchors follow
        // the GitHub slug rule: lowercase, spaces to hyphens, punctuation
        // dropped.
        sb.Append("\nSections: ");
        bool first = true;
        foreach (string category in OrderedCategories(entries))
        {
            if (!first) sb.Append(" · ");
            first = false;
            string anchor = new string(category.ToLowerInvariant()
                .Select(ch => ch == ' ' ? '-' : ch)
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '-')
                .ToArray());
            sb.Append('[').Append(category).Append("](#").Append(anchor).Append(')');
        }
        sb.Append('\n');

        foreach (string category in OrderedCategories(entries))
        {
            sb.Append("\n## ").Append(category).Append("\n\n");
            foreach (var (cat, library, method) in CategoryLibrary)
            {
                if (cat != category) continue;
                sb.Append("Load with `:- use_module(library(").Append(library)
                  .Append(")).` (embedding: `engine.").Append(method)
                  .Append("()`).\n\n");
                break;
            }
            sb.Append("| Predicate | Description |\n");
            sb.Append("| --- | --- |\n");
            IEnumerable<Entry> inCategory = entries
                .Where(e => e.Category == category)
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ThenBy(e => e.Arity);
            foreach (Entry e in inCategory)
                sb.Append("| `").Append(e.Template).Append("` | ")
                  .Append(e.Summary.Replace("|", "\\|")).Append(" |\n");
        }
        return sb.ToString();
    }

    private static IEnumerable<string> OrderedCategories(List<Entry> entries)
    {
        var present = entries.Select(e => e.Category).ToHashSet();
        foreach (string c in CategoryOrder)
            if (present.Remove(c)) yield return c;
        foreach (string c in present.OrderBy(c => c, StringComparer.Ordinal))
            yield return c;
    }
}
