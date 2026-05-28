using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 209: a <c>:- dynamic foo/N.</c> predicate that ships with source
/// clauses must dispatch correctly when loaded from a bundle — including a
/// Release/stripped bundle where the source is gone. The clauses ride along
/// as TermCodec-encoded "dynamic seeds" and are re-asserted into the
/// engine's dynamic store at load. This was the Blint regression: its
/// <c>main</c> is declared <c>:- dynamic main/0.</c> with a body clause, and
/// the bundle dispatched an empty trampoline before this fix.
/// </summary>
public class DynamicBundleSeedTests
{
    private static Bundle LinkSource(string source, params PredicateRef[] entries)
    {
        var obj = ShmoCompiler.CompileSource(source, "test",
            ShmoBuildMode.Release);
        var config = new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = entries,
        };
        var result = ShmoLinker.Link(config);
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        return BundleReader.FromBytes(result.Bytes!);
    }

    [Fact]
    public void DynamicPredicate_WithClauses_DispatchesFromBundle()
    {
        // main is dynamic AND has a body — the Blint shape.
        var bundle = LinkSource(
            ":- dynamic main/0.\n"
            + "greeting(hello).\n"
            + "main :- greeting(X), write(X), nl.\n",
            new PredicateRef("main", 0));
        var engine = new PrologEngine();
        engine.LoadBundle(bundle);
        Assert.True(engine.Query("main.").Success);
    }

    [Fact]
    public void DynamicPredicate_InsideCatch_LocalReachable()
    {
        // The protected goal of catch/3 calls a module-local predicate.
        // Chunk-209 CollectCalls descent keeps it reachable; the dynamic
        // seed + userLocals fold keep it mangled-consistent at dispatch.
        var bundle = LinkSource(
            ":- dynamic main/0.\n"
            + "ver('1.0').\n"
            + "main :- catch((ver(V), write(V), nl), _, true).\n",
            new PredicateRef("main", 0));
        var engine = new PrologEngine();
        engine.LoadBundle(bundle);
        Assert.True(engine.Query("main.").Success);
    }

    [Fact]
    public void DynamicPredicate_FromBundle_StillMutable()
    {
        // The seeded clauses behave like any dynamic predicate: assertz /
        // retract / clause-2 all see them.
        var bundle = LinkSource(
            ":- dynamic item/1.\n"
            + "item(a).\n"
            + "item(b).\n"
            + ":- public count_items/1.\n"
            + "count_items(N) :- findall(X, item(X), L), length(L, N).\n",
            new PredicateRef("count_items", 1));
        var engine = new PrologEngine();
        engine.LoadBundle(bundle);

        // Seeded clauses visible.
        Assert.Equal("2", engine.Query("count_items(N).").Bindings["N"].ToString());

        // assertz a third, retract one — both must take effect.
        Assert.True(engine.Query("assertz(item(c)).").Success);
        Assert.Equal("3", engine.Query("count_items(N).").Bindings["N"].ToString());
        Assert.True(engine.Query("retract(item(a)).").Success);
        Assert.Equal("2", engine.Query("count_items(N).").Bindings["N"].ToString());
    }

    [Fact]
    public void NonDynamicPredicate_FromBundle_StillStatic()
    {
        // Regression guard: a plain (non-dynamic) predicate must still
        // dispatch via the static path, unaffected by the dynamic-seed
        // machinery.
        var bundle = LinkSource(
            ":- public main/0.\n"
            + "main :- write(ok), nl.\n",
            new PredicateRef("main", 0));
        var engine = new PrologEngine();
        engine.LoadBundle(bundle);
        Assert.True(engine.Query("main.").Success);
    }
}
