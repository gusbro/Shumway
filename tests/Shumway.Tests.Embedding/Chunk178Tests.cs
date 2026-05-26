using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 15 chunk 178: source-less LoadBundle. With chunks 172
/// (linker --strip) and 176 (ShmoCompiler applies ModuleRewrite),
/// a Bundle that carries the per-module CompiledBytecode + the
/// Defined visibility list — but no embedded Prolog source — now
/// loads into the engine and dispatches identically to the
/// source-bearing path. Closes Phase 14's chunk-172 "stripped
/// bundles fail at runtime" limitation.
/// </summary>
public class Chunk178Tests
{
    [Fact]
    public void StrippedBundle_PublicPredicate_Dispatches()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- module(m).\n"
            + ":- public foo/0.\n"
            + "foo :- write(hi).\n", "m");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("foo", 0) },
            StripSource = true,
        });
        Assert.True(result.Success);
        var entry = Assert.Single(result.Bundle!.Entries);
        Assert.Equal("", entry.Source);

        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        Assert.True(engine.Query("foo.").Success);
    }

    [Fact]
    public void StrippedBundle_LocalPredicate_DispatchesViaPublicCaller()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- module(m).\n"
            + ":- public entry/1.\n"
            + "entry(X) :- helper(X).\n"
            + "helper(answer).\n", "m");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("entry", 1) },
            StripSource = true,
        });
        Assert.True(result.Success);

        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        var sol = engine.Query("entry(X).");
        Assert.True(sol.Success);
        Assert.Equal("answer", sol.Bindings["X"].ToString());
    }

    [Fact]
    public void StrippedBundle_FactsBacktrack()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- module(m).\n"
            + ":- public color/1.\n"
            + "color(red). color(green). color(blue).\n", "m");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("color", 1) },
            StripSource = true,
        });
        Assert.True(result.Success);

        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);

        var collected = new List<string>();
        foreach (var sol in engine.QueryAll("color(X)."))
            collected.Add(sol.Bindings["X"].ToString()!);
        Assert.Equal(new[] { "red", "green", "blue" }, collected);
    }

    [Fact]
    public void StrippedBundle_BundleRoundTripsThroughBytes()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- module(m).\n"
            + ":- public sum/3.\n"
            + "sum(A, B, S) :- S is A + B.\n", "m");
        var built = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("sum", 3) },
            StripSource = true,
        });
        Assert.True(built.Success);

        // Round-trip through the on-disk bundle format (V2 with
        // Defined). Reader should preserve the strip + visibility.
        byte[] bytes = BundleWriter.ToBytes(
            built.Bundle!, includeCompiledBytecode: false);
        var reread = BundleReader.FromBytes(bytes);
        var entry = Assert.Single(reread.Entries);
        Assert.Equal("", entry.Source);
        Assert.NotEmpty(entry.Defined);
        Assert.Contains(entry.Defined,
            d => d.Indicator.Name == "sum" && d.Indicator.Arity == 3
                 && d.Visibility == PredicateVisibility.Public);

        var engine = new PrologEngine();
        engine.LoadBundle(reread);
        var sol = engine.Query("sum(2, 3, X).");
        Assert.True(sol.Success);
        Assert.Equal("5", sol.Bindings["X"].ToString());
    }
}
