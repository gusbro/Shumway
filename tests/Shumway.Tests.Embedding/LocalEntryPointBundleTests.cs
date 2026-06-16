using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A LOCAL entry-point predicate (no <c>:- public</c>, no
/// <c>:- module</c> → mangled <c>module$name</c>) must be callable by its bare
/// name from a bundle, including a SOURCE-STRIPPED (release) WAM-only bundle.
///
/// <para>Regression: <c>shumway-link -s -g main</c> on a release <c>.shmo</c>
/// (no <c>--with-compiled-il</c>) produced an exe that raised
/// <c>existence_error(procedure, main/0)</c>. The chunk-200 entry promotion
/// (augment source with <c>:- public</c> + recompile) can't run without source,
/// so <c>main</c> stays local; it then resolves only through the bare-name alias
/// (AddBareLocalAliases) to the mangled WAM body — but
/// <c>ResolveTargetMaybeAutoPromoted</c> accepted only an <c>enter_dynamic</c>
/// trampoline or an IL resume marker, never a plain WAM address. So the IL bundle
/// (<c>--strip-wam</c>) worked while the WAM-only bundle didn't.</para></summary>
public sealed class LocalEntryPointBundleTests
{
    // main/0 is local; it calls another local (aux/1) so the test also covers
    // intra-bundle local dispatch from the bare-named entry.
    private const string Program =
        "main :- aux(R), R == ok.\n" +
        "aux(ok).\n";

    private static byte[] LinkWamOnly(ShmoBuildMode mode) =>
        ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Program, "prog", mode) },
            EntryPoints = new[] { new PredicateRef("main", 0) },
            StripSource = true,
            BakePrelude = true,
            IncludeCompiledIl = false,
        }).Bytes!;

    [Theory]
    [InlineData(ShmoBuildMode.Release)]  // source already stripped — the fixed case
    [InlineData(ShmoBuildMode.Debug)]    // source-bearing → linker's :- public augment path
    public void LocalEntry_BareName_WamOnlyBundle(ShmoBuildMode mode)
    {
        var engine = PrologEngine.FromBundle(BundleReader.FromBytes(LinkWamOnly(mode)));
        Assert.True(engine.Query("main.").Success);
        // Also reachable through a runtime meta-call by bare name.
        Assert.True(engine.Query("catch(main, _, fail).").Success);
    }

    // Multi-module: app's local entry main/1 calls util's public greet/2, and
    // BOTH modules define a local tag/1 with the SAME name. Promoting the entry
    // must not leak visibility — util's greet sees util's tag, app's main sees
    // app's tag. Source-stripped WAM-only bundle (the fixed path).
    private const string Util =
        ":- module(util).\n" +
        ":- public greet/2.\n" +
        "greet(Name, Out) :- tag(T), atom_concat(T, Name, Out).\n" +
        "tag('util_').\n";
    private const string App =
        "main(R) :- greet(alice, G), tag(T), R = G-T.\n" +
        "tag('app_').\n";

    [Fact]
    public void MultiModule_LocalEntry_PreservesVisibility()
    {
        byte[] bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource(Util, "util", ShmoBuildMode.Release),
                ShmoCompiler.CompileSource(App, "app", ShmoBuildMode.Release),
            },
            EntryPoints = new[] { new PredicateRef("main", 1) },
            StripSource = true,
            BakePrelude = true,
            IncludeCompiledIl = false,
        }).Bytes!;
        var engine = PrologEngine.FromBundle(BundleReader.FromBytes(bytes));
        var sols = engine.QueryAll("main(R).").ToList();
        Assert.Single(sols);
        // util's greet used util's tag; app's main used app's tag — distinct.
        // (R is the compound 'util_alice'-'app_', rendered in functional form.)
        Assert.Equal("-(util_alice, app_)", sols[0].Bindings["R"].ToString());
        // util's local tag/1 stays hidden from the top level (only main/1 was promoted).
        Assert.False(engine.Query("catch(tag(_), _, fail).").Success);
    }
}
