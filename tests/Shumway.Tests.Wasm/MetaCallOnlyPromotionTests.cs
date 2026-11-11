using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A predicate the program reaches only through a meta-call enters
/// the tier as one it calls directly does.</summary>
public sealed class MetaCallOnlyPromotionTests(ITestOutputHelper o)
{
    private const string Corpus = """
        a(1).
        a(2).
        nc(_) :- !.
        nc(_) :- fail.
        rtcut(X) :- G =.. [',', a(X), nc(X)], call(G).
        direct(X) :- a(X), nc(X).
        """;

    private static int Fid(PrologEngine engine, string name, int arity)
    {
        foreach (var (_, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (aid, ar) = FunctorTable.Lookup(pred.FunctorId);
            string n = AtomTable.GetById(aid)?.Name ?? "";
            if (ar == arity && (n == name || n.EndsWith("$" + name))) return pred.FunctorId;
        }
        throw new Xunit.Sdk.XunitException($"{name}/{arity} is not a static predicate");
    }

    [Theory]
    [InlineData("rtcut")]
    [InlineData("direct")]
    public void TheCalleesEnterTheTier(string caller)
    {
        var (engine, _, world) = TieredEngine.BuildWithWorld(Corpus);
        for (int i = 0; i < 3; i++)
            Assert.True(engine.Query($"findall(X, {caller}(X), [1,2]).").Success);
        int c = Fid(engine, caller, 1), a = Fid(engine, "a", 1), nc = Fid(engine, "nc", 1);
        o.WriteLine($"{caller}: caller={world.Contains(c)} a={world.Contains(a)} nc={world.Contains(nc)}");
        Assert.True(world.Contains(c), $"{caller}/1 is not on the tier");
        Assert.True(world.Contains(a), "a/1 is not on the tier");
        Assert.True(world.Contains(nc), "nc/1 is not on the tier");
    }

    [Fact]
    public void ADirectCallAfterTheMetaCallsPromotesTheCallee()
    {
        var (engine, _, world) = TieredEngine.BuildWithWorld(Corpus);
        for (int i = 0; i < 3; i++)
            Assert.True(engine.Query("findall(X, rtcut(X), [1,2]).").Success);
        for (int i = 0; i < 3; i++)
            Assert.True(engine.Query("findall(X, a(X), [1,2]).").Success);
        int a = Fid(engine, "a", 1);
        o.WriteLine($"a={world.Contains(a)}");
        Assert.True(world.Contains(a), "a/1 is not on the tier after direct calls");
    }
}
