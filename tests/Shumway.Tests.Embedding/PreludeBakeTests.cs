using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Baked-prelude bundles + the PrologEngine.FromBundle bare-load path
/// (the fast-startup mechanism: a deployed bundle carries its precompiled
/// prelude so loading it skips parsing + compiling the prelude).</summary>
public sealed class PreludeBakeTests
{
    // Uses member/2 — a prelude predicate — so a working run proves the
    // prelude is present and dispatches, however it was supplied.
    private const string Src = ":- public run/0.\nrun :- member(b, [a, b, c]).\n";

    private static byte[] Link(bool bake)
    {
        var r = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Src, "app") },
            EntryPoints = new[] { new PredicateRef("run", 0) },
            BakePrelude = bake,
        });
        Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));
        return r.Bytes!;
    }

    [Fact]
    public void BakePrelude_AddsPreludeEntry()
    {
        var bundle = BundleReader.FromBytes(Link(bake: true));
        Assert.Contains(bundle.Entries, e => e.ModuleName == "$prelude");
    }

    [Fact]
    public void NoBake_HasNoPreludeEntry()
    {
        var bundle = BundleReader.FromBytes(Link(bake: false));
        Assert.DoesNotContain(bundle.Entries, e => e.ModuleName == "$prelude");
    }

    [Fact]
    public void FromBundle_WithBakedPrelude_Runs()
    {
        // A bare engine takes the prelude from the bundle.
        var engine = PrologEngine.FromBundle(BundleReader.FromBytes(Link(bake: true)));
        Assert.True(engine.Query("run.").Success);
    }

    [Fact]
    public void FromBundle_WithoutBakedPrelude_FallsBackToConsult()
    {
        // No baked prelude — FromBundle consults the prelude itself.
        var engine = PrologEngine.FromBundle(BundleReader.FromBytes(Link(bake: false)));
        Assert.True(engine.Query("run.").Success);
    }

    [Fact]
    public void NormalEngine_DropsRedundantBakedPrelude()
    {
        // A normal engine already consulted the prelude; loading a bundle that
        // also bakes one must drop the redundant entry and still run.
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(Link(bake: true)));
        Assert.True(engine.Query("run.").Success);
    }
}
