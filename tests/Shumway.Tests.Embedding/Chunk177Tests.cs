using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 15 chunk 177: <c>shumway-compile -r</c> (Release) drops both
/// the embedded Prolog source AND the per-clause Meta/DbgInfo bytecode
/// markers from the .shmo. Combined with chunk 178's source-less
/// LoadBundle path, a Release artifact carries no recoverable debug
/// information at all and still runs at full speed.
/// </summary>
public class Chunk177Tests
{
    private const string Src =
        ":- module(m).\n"
        + ":- public foo/1.\n"
        + "foo(1). foo(2). foo(3).\n";

    [Fact]
    public void Release_StripsSourceFromShmo()
    {
        var release = ShmoCompiler.CompileSource(Src, "m", ShmoBuildMode.Release);
        Assert.Equal("", release.Source);
    }

    [Fact]
    public void Debug_PreservesSourceInShmo()
    {
        var debug = ShmoCompiler.CompileSource(Src, "m", ShmoBuildMode.Debug);
        Assert.Equal(Src, debug.Source);
    }

    [Fact]
    public void Release_BytecodeIsSmallerThanDebug()
    {
        var release = ShmoCompiler.CompileSource(Src, "m", ShmoBuildMode.Release);
        var debug = ShmoCompiler.CompileSource(Src, "m", ShmoBuildMode.Debug);
        // Three clauses → three 6-byte Meta opcodes elided in Release.
        Assert.Equal(18, debug.Bytecode.Length - release.Bytecode.Length);
    }

    [Fact]
    public void Release_LinkedBundleRunsCorrectly()
    {
        var obj = ShmoCompiler.CompileSource(Src, "m", ShmoBuildMode.Release);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("foo", 1) },
        });
        Assert.True(result.Success);
        var entry = Assert.Single(result.Bundle!.Entries);
        // Source stripped at compile time (Release) — the linker doesn't
        // need StripSource to flip back since it was empty already.
        Assert.Equal("", entry.Source);

        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        var collected = new List<int>();
        foreach (var sol in engine.QueryAll("foo(X)."))
            collected.Add(int.Parse(sol.Bindings["X"].ToString()!));
        Assert.Equal(new[] { 1, 2, 3 }, collected);
    }

    [Fact]
    public void Release_NoMetaOpcodeInBytecode()
    {
        var obj = ShmoCompiler.CompileSource(Src, "m", ShmoBuildMode.Release);
        // Decode the bytecode and scan every predicate. Meta is opcode
        // 0xFE — must not appear in any Release predicate.
        var module = CompiledModuleCodec.Decode(obj.Bytecode);
        foreach (var pred in module.Predicates)
            foreach (byte b in pred.Bytecode)
                Assert.NotEqual((byte)Shumway.Core.Opcode.Meta, b);
    }
}
