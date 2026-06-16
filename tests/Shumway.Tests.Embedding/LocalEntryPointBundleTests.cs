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
}
