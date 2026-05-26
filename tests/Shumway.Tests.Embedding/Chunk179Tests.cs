using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 15 chunk 179: <c>shumway-link --strip</c> propagates the
/// Release-mode debug strip down to the linked .shum. A Debug .shmo
/// compiled with debug info, linked with <c>--strip</c>, produces a
/// .shum whose bytecode has no Meta/DbgInfo opcodes — same shape as
/// if the .shmo had been compiled with <c>-r</c> directly. And the
/// linker no longer emits the chunk-172 "stripped_bundle" warning,
/// since chunk-178 made stripped bundles actually run.
/// </summary>
public class Chunk179Tests
{
    private const string Src =
        ":- module(m).\n"
        + ":- public foo/1.\n"
        + "foo(a). foo(b). foo(c).\n";

    [Fact]
    public void DebugShmoLinkedWithStrip_BytecodeHasNoMetaOpcode()
    {
        // Compile in Debug — bytecode includes Meta opcodes.
        var obj = ShmoCompiler.CompileSource(Src, "m", ShmoBuildMode.Debug);
        Assert.NotEqual("", obj.Source);

        // Sanity: Debug bytecode HAS Meta.
        var debugDecoded = CompiledModuleCodec.Decode(obj.Bytecode);
        bool debugHasMeta = false;
        foreach (var pred in debugDecoded.Predicates)
            foreach (byte b in pred.Bytecode)
                if (b == (byte)Shumway.Core.Opcode.Meta) { debugHasMeta = true; break; }
        Assert.True(debugHasMeta, "Debug bytecode must carry Meta opcodes");

        // Now link with --strip and check the entry's bytecode.
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("foo", 1) },
            StripSource = true,
        });
        Assert.True(result.Success);
        var entry = Assert.Single(result.Bundle!.Entries);
        Assert.Equal("", entry.Source);

        var strippedDecoded = CompiledModuleCodec.Decode(entry.CompiledBytecode!);
        foreach (var pred in strippedDecoded.Predicates)
            foreach (byte b in pred.Bytecode)
                Assert.NotEqual((byte)Shumway.Core.Opcode.Meta, b);
    }

    [Fact]
    public void StrippedBundle_NoStrippedBundleWarning()
    {
        // Chunk 178 made source-less bundles runnable; the chunk-172
        // "stripped_bundle" warning should be gone.
        var obj = ShmoCompiler.CompileSource(Src, "m", ShmoBuildMode.Debug);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("foo", 1) },
            StripSource = true,
        });
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Code == "stripped_bundle");
    }

    [Fact]
    public void DebugShmoLinkedWithStrip_RunsAtRuntime()
    {
        var obj = ShmoCompiler.CompileSource(Src, "m", ShmoBuildMode.Debug);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("foo", 1) },
            StripSource = true,
        });
        Assert.True(result.Success);

        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        var collected = new List<string>();
        foreach (var sol in engine.QueryAll("foo(X)."))
            collected.Add(sol.Bindings["X"].ToString()!);
        Assert.Equal(new[] { "a", "b", "c" }, collected);
    }

    [Fact]
    public void ReleaseShmoLinkedWithStrip_PassesThroughUnchanged()
    {
        // The .shmo was already stripped at compile time — the linker
        // shouldn't recompile from a non-existent source.
        var obj = ShmoCompiler.CompileSource(Src, "m", ShmoBuildMode.Release);
        Assert.Equal("", obj.Source);

        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("foo", 1) },
            StripSource = true,
        });
        Assert.True(result.Success);
        var entry = Assert.Single(result.Bundle!.Entries);
        // Bytecode survives intact.
        Assert.Equal(obj.Bytecode.Length, entry.CompiledBytecode!.Length);

        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        Assert.True(engine.Query("foo(X).").Success);
    }
}
