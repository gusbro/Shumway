using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Promoting variables to attributed ones from the module, many in
/// one chain, as a solver posting constraints does. Two things sent those
/// writes back to the host one by one: the extra trail left no room above
/// its top, and the attribute image's insert budget was capped at sixteen.
/// Counted by the reason each request carries.</summary>
public sealed class AttrInsertBudgetTests(ITestOutputHelper o)
{
    private const string Corpus = """
        att_put(V, A) :- '$put_to_attr_list'(V, m, A).
        promote_many(0, []) :- !.
        promote_many(N, [V|Vs]) :- att_put(V, k(N)), N1 is N - 1, promote_many(N1, Vs).
        """;

    private (long Requests, Dictionary<int, long> Reasons) Run()
    {
        var (engine, members) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query("promote_many(400, _).").Success);    // warm: promote
        Assert.NotEmpty(members);
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("promote_many(400, Vs), length(Vs, 400).").Success);

        var reasons = new Dictionary<int, long>();
        foreach (var (key, hits) in WasmTierDelegate.DiagRequestReasons)
            if (Shumway.Builtins.BuiltinsRegistry.GetById((int)(key >> 8)).Name == "$put_to_attr_list")
                reasons[(int)(key & 0xFF)] = reasons.GetValueOrDefault((int)(key & 0xFF)) + hits;
        long requests = WasmTierDelegate.BuiltinRanking()
            .Where(b => b.Name == "$put_to_attr_list").Sum(b => b.Hits);
        o.WriteLine($"requests={requests} "
            + string.Join(", ", reasons.Select(r => $"{r.Value} x {r.Key}")));
        return (requests, reasons);
    }

    /// <summary>The module reserves two extra-trail entries per write and the
    /// host grew the trail only for its own pushes, which on a young variable
    /// there are none of: a trail that stopped short of full stalled every
    /// write after it. The host now leaves the module room at each staging.
    /// </summary>
    [DiagFact]
    public void WritesDoNotStallOnTheExtraTrail()
    {
        var run = Run();
        Assert.True(run.Reasons.GetValueOrDefault(47) <= 5,
            $"{run.Reasons.GetValueOrDefault(47)} writes found no room on the extra trail");
    }

    /// <summary>The insert budget is the distance to the image's rebuild
    /// line, all of it: a cap of sixteen let a chain promote eight variables
    /// and sent each further one to the host to be given sixteen more, 757
    /// times in one clp(Z) sudoku solve. Growing the table is log-many.
    /// </summary>
    [DiagFact]
    public void ALongChainOfPromotionsStaysInTheModule()
    {
        var run = Run();
        Assert.True(run.Reasons.GetValueOrDefault(49) <= 10,
            $"{run.Reasons.GetValueOrDefault(49)} promotions were sent back for budget");
        // Anti-vacuity: the promotions ran in the module, or there would be
        // 400 requests.
        Assert.True(run.Requests < 40, $"{run.Requests} of 400 promotions left the module");
    }
}
