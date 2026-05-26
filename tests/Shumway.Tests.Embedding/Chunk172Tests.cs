using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 14 chunk 172: <c>shumway-link --strip</c> removes the
/// embedded Prolog source from every bundle entry. The compiled
/// bytecode is preserved.
///
/// <para>Current limitation: <see cref="PrologEngine.LoadBundle(Bundle)"/>
/// re-consults the source to register clauses with the static
/// program. Stripped bundles still load but predicate dispatch
/// raises <c>existence_error/2</c>. A source-less load path is
/// queued for a future chunk; the strip flag is provided ahead of
/// it for size / IP-protection workflows.</para>
/// </summary>
public class Chunk172Tests
{
    [Fact]
    public void Strip_ReplacesSourceWithEmpty()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- module(m).\n:- public foo/1.\nfoo(1). foo(2). foo(3).\n", "m");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("foo", 1) },
            StripSource = true,
        });
        Assert.True(result.Success);
        Assert.NotNull(result.Bundle);
        var entry = Assert.Single(result.Bundle!.Entries);
        Assert.Equal("", entry.Source);
        Assert.NotNull(entry.CompiledBytecode);
        Assert.NotEmpty(entry.CompiledBytecode!);
    }

    [Fact]
    public void Strip_NoStrippedBundleWarning_AfterChunk179()
    {
        // Chunk 179: the "stripped_bundle" warning is gone — chunk 178
        // made the source-less LoadBundle path real, so stripping is
        // no longer a runtime liability that needs flagging.
        var obj = ShmoCompiler.CompileSource(":- module(m).\n:- public p/0.\np.\n", "m");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("p", 0) },
            StripSource = true,
        });
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Code == "stripped_bundle");
    }

    [Fact]
    public void NoStrip_PreservesSource()
    {
        // Compile in Debug so the source survives the compile step
        // (chunk 177: Release already strips at compile time).
        // The linker's StripSource flag is what we're testing here.
        var obj = ShmoCompiler.CompileSource(":- module(m).\n:- public p/0.\np.\n",
            "m", ShmoBuildMode.Debug);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("p", 0) },
        });
        var entry = Assert.Single(result.Bundle!.Entries);
        Assert.NotEqual("", entry.Source);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "stripped_bundle");
    }

    [Fact]
    public void Strip_ShrinksBundleBytes()
    {
        // Big-ish source: confirm the strip actually saves bytes.
        // Debug build so the source survives the compile (chunk 177
        // would strip it under Release).
        var src = ":- module(big).\n:- public f/1.\n"
            + string.Concat(Enumerable.Range(0, 200).Select(i => $"f({i}) :- f({i + 1}).\n"));
        var obj = ShmoCompiler.CompileSource(src, "big", ShmoBuildMode.Debug);
        var withSource = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("f", 1) },
            AllowUndefined = true,   // f(201) is not defined; not the point of the test.
            StripSource = false,
        });
        var stripped = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("f", 1) },
            AllowUndefined = true,
            StripSource = true,
        });
        Assert.True(stripped.Bytes!.Length < withSource.Bytes!.Length);
    }

    [Fact]
    public void LinkFromSources_StripSource_Honored()
    {
        var result = ShmoLinker.LinkFromSources(
            sources: new[]
            {
                ("m", ":- module(m).\n:- public p/0.\np.\n"),
            },
            entryPoints: new[] { new PredicateRef("p", 0) },
            stripSource: true);
        Assert.True(result.Success);
        Assert.Equal("", result.Bundle!.Entries[0].Source);
    }
}
