using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 18 — the linker accepts entry-point predicates that aren't
/// declared <c>:- public</c>. The flow:
/// <list type="number">
/// <item>If the entry resolves to a public or dynamic predicate in any
///   linked module, use that (pre-Phase-18 behaviour).</item>
/// <item>Else scan every linked module's local definitions for a match.
///   Exactly one → promote it transparently: prepend
///   <c>:- public &lt;entry&gt;.</c> to that module's bundled source and
///   drop the pre-compiled bytecode (force LoadBundle's ConsultString to
///   recompile under the now-public-marked source). The runtime query
///   then finds the entry by its bare name without the
///   <c>module$pred/N</c> mangling.</item>
/// <item>Two or more locals → explicit
///   <c>ambiguous_entry</c> link error naming the colliding modules.</item>
/// </list>
/// </summary>
public class LocalEntryPointTests
{
    [Fact]
    public void LocalEntry_SingleModule_LinksAndRuns()
    {
        // Plain top-level program — no `:- module(...)`, no `:- public`.
        // Pre-Phase-18 the linker would have errored "Entry point
        // greet/0 is not defined as :- public or :- dynamic".
        var shmo = ShmoCompiler.CompileSource(
            "greet :- true.\n",
            "single", ShmoBuildMode.Debug);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { shmo },
            EntryPoints = new[] { new PredicateRef("greet", 0) },
        });
        Assert.True(result.Success, FormatDiagnostics(result));

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        Assert.True(engine.Query("greet.").Success);
    }

    [Fact]
    public void LocalEntry_AmbiguousAcrossModules_FailsWithExplicitMessage()
    {
        var modA = ShmoCompiler.CompileSource(
            ":- module(modA).\nshared(a).\n",
            "modA", ShmoBuildMode.Debug);
        var modB = ShmoCompiler.CompileSource(
            ":- module(modB).\nshared(b).\n",
            "modB", ShmoBuildMode.Debug);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { modA, modB },
            EntryPoints = new[] { new PredicateRef("shared", 1) },
        });
        Assert.False(result.Success);
        var err = result.Diagnostics.FirstOrDefault(
            d => d.Severity == LinkSeverity.Error && d.Code == "ambiguous_entry");
        Assert.NotNull(err);
        Assert.Contains("'modA'", err!.Message);
        Assert.Contains("'modB'", err.Message);
    }

    [Fact]
    public void LocalEntry_PreferredOverPublic_WhenOnlyLocalExists()
    {
        // Sanity that the existing :- public path still works (Phase 18
        // is purely additive — it only kicks in when no public match
        // exists).
        var shmo = ShmoCompiler.CompileSource(
            ":- public greet/0.\ngreet :- true.\n",
            "pubmod", ShmoBuildMode.Debug);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { shmo },
            EntryPoints = new[] { new PredicateRef("greet", 0) },
        });
        Assert.True(result.Success, FormatDiagnostics(result));

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        Assert.True(engine.Query("greet.").Success);
    }

    [Fact]
    public void LocalEntry_NotFound_ReportsEntryNotFound()
    {
        var shmo = ShmoCompiler.CompileSource(
            "something_else.\n", "x", ShmoBuildMode.Debug);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { shmo },
            EntryPoints = new[] { new PredicateRef("missing", 0) },
        });
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == LinkSeverity.Error && d.Code == "entry_not_found");
    }

    private static string FormatDiagnostics(LinkResult result) =>
        string.Join("\n", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Code}: {d.Message}"));
}
