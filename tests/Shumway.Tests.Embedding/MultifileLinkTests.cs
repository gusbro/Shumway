using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// `:- multifile` through the compile/link/load path: several modules
/// contribute clauses to one predicate (the CLP(FD)+CLP(R)
/// verify_attributes/4 shape). Each module's contributions are
/// module-rewritten at COMPILE time under their origin module and the seed
/// carries a Multifile flag, so the load path never applies a single
/// per-fid seed-module context to a fid that holds several modules'
/// clauses.
/// </summary>
public sealed class MultifileLinkTests
{
    // Both modules define a LOCAL helper/1 with the same bare name — the
    // collision that proves the origin-module pre-mangling: hook(a,_)'s body
    // must reach ma's helper, hook(b,_)'s must reach mb's.
    private const string ModA =
        ":- module(ma).\n" +
        ":- public hook/2.\n" +
        ":- multifile hook/2.\n" +
        "hook(a, X) :- helper(X).\n" +
        "helper(ma_result).\n";

    private const string ModB =
        ":- module(mb).\n" +
        ":- public hook/2.\n" +
        ":- multifile hook/2.\n" +
        "hook(b, X) :- helper(X).\n" +
        "helper(mb_result).\n";

    private const string Main =
        ":- module(main).\n" +
        ":- public run/2.\n" +
        "run(K, X) :- hook(K, X).\n";

    [Fact]
    public void MultifileSeed_FlagAndPreMangling_RoundTripThroughShmo()
    {
        var obj = ShmoCompiler.CompileSource(ModA, "ma", ShmoBuildMode.Release);

        var seed = Assert.Single(obj.DynamicSeeds);
        Assert.Equal("hook", seed.Indicator.Name);
        Assert.Equal(2, seed.Indicator.Arity);
        Assert.True(seed.Multifile);
        // The clause body's helper/1 call was pre-mangled under ma.
        var clause = TermCodec.DecodeClause(seed.EncodedClauses[0]);
        Assert.Contains("ma$helper", clause.Term.ToString());

        var back = ShmoReader.FromBytes(ShmoWriter.ToBytes(obj));
        var backSeed = Assert.Single(back.DynamicSeeds);
        Assert.True(backSeed.Multifile);
        Assert.Equal(seed.EncodedClauses[0], backSeed.EncodedClauses[0]);
    }

    [Fact]
    public void TwoModules_ShareOneMultifilePredicate_NoDuplicateError()
    {
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource(ModA, "ma", ShmoBuildMode.Release),
                ShmoCompiler.CompileSource(ModB, "mb", ShmoBuildMode.Release),
                ShmoCompiler.CompileSource(Main, "main", ShmoBuildMode.Release),
            },
            EntryPoints = new[] { new PredicateRef("run", 2) },
            BakePrelude = false,
            IncludeCompiledIl = false,
        });

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "duplicate_public");
        // Both contributing modules survive reachability (neither dropped).
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "unreachable_module");
    }

    [Fact]
    public void LinkedBundle_DispatchesEachContribution_UnderItsOriginModule()
    {
        byte[] bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource(ModA, "ma", ShmoBuildMode.Release),
                ShmoCompiler.CompileSource(ModB, "mb", ShmoBuildMode.Release),
                ShmoCompiler.CompileSource(Main, "main", ShmoBuildMode.Release),
            },
            EntryPoints = new[] { new PredicateRef("run", 2) },
            BakePrelude = false,
            IncludeCompiledIl = false,
        }).Bytes!;

        // Release objects are source-stripped, so LoadBundle takes the
        // source-less seed-rehydration path — the one the fix targets.
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));

        Assert.Equal("ma_result", engine.Query("run(a, X).")["X"]!.ToString());
        Assert.Equal("mb_result", engine.Query("run(b, X).")["X"]!.ToString());
    }

    [Fact]
    public void LinkedBundle_BacktracksAcrossContributingModules()
    {
        // A call with an unbound key enumerates BOTH modules' clauses —
        // the two contributions live in one clause chain.
        byte[] bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource(ModA, "ma", ShmoBuildMode.Release),
                ShmoCompiler.CompileSource(ModB, "mb", ShmoBuildMode.Release),
                ShmoCompiler.CompileSource(Main, "main", ShmoBuildMode.Release),
            },
            EntryPoints = new[] { new PredicateRef("run", 2) },
            BakePrelude = false,
            IncludeCompiledIl = false,
        }).Bytes!;

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));

        var sols = engine.QueryAll("run(K, X).").ToList();
        Assert.Equal(2, sols.Count);
        Assert.Equal("ma_result", sols[0]["X"]!.ToString());
        Assert.Equal("mb_result", sols[1]["X"]!.ToString());
    }
}
