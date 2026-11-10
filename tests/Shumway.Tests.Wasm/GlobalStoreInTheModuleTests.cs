using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>b_setval/2 (and Scryer's bb_b_put/2, which clp(Z) keeps its
/// propagation state in) made by the module: the row of the global-variable
/// image takes the value, the write is parked for the host with the old value
/// beside it, and the MutableSet trail entry the module writes carries the
/// log index the host's record will have, so backtracking puts the old value
/// back. A key's first write is still the host's. Tier-0 is the oracle.
/// </summary>
public sealed class GlobalStoreInTheModuleTests(ITestOutputHelper o)
{
    private const string Corpus = """
        bs_twice(V) :- b_setval(k, 1), b_setval(k, 2), b_getval(k, V).
        bs_undone(R) :- b_setval(k, 0), ( member(X, [1, 2]), b_setval(k, X), fail ; true ), b_getval(k, R).
        bs_bound_later(R) :- b_setval(k, 0), b_setval(k, X), X = f(a), b_getval(k, R).
        bs_cut(R) :- b_setval(k, 0), ( member(X, [1, 2, 3]), b_setval(k, X), X >= 2, ! ; true ), b_getval(k, R).
        bs_inner :- b_setval(k, 9), fail.
        bs_outer(R) :- b_setval(k, 0), ( bs_inner ; true ), b_getval(k, R).
        bs_thrown(R) :- b_setval(k, 0), catch(bs_throw, x, true), b_getval(k, R).
        bs_throw :- b_setval(k, 5), throw(x).
        bs_scryer(R) :- '$store_backtrackable_global_var'(s, 1),
                        '$store_backtrackable_global_var'(s, 2),
                        '$fetch_global_var'(s, R).
        bs_loop(N) :- b_setval(k, 0), bs_loop_(N).
        bs_loop_(0) :- !.
        bs_loop_(N) :- b_setval(k, N), b_getval(k, N), N1 is N - 1, bs_loop_(N1).
        """;

    private (string Answer, long Requests) Run(string goal)
    {
        var (engine, members) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query(goal).Success, goal);      // warm: promote
        Assert.NotEmpty(members);
        WasmTierDelegate.ResetDiag();
        string answer = Answer(engine.Query(goal));
        long requests = WasmTierDelegate.BuiltinRanking()
            .Where(b => b.Name is "b_setval" or "$store_backtrackable_global_var").Sum(b => b.Hits);
        o.WriteLine($"{goal}: {answer} requests={requests} deopts={WasmTierDelegate.DiagDeopts}");
        return (answer, requests);
    }

    private static string Answer(Solution r)
        => r.Success ? string.Join(",", r.Bindings.Select(b => $"{b.Key}={b.Value}")) : "fail";

    private static string Tier0(string goal)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.ConsultString(Corpus);
        return Answer(e.Query(goal));
    }

    [DiagTheory]
    [InlineData("bs_twice(V).")]
    [InlineData("bs_undone(R).")]
    [InlineData("bs_bound_later(R).")]
    [InlineData("bs_cut(R).")]
    [InlineData("bs_outer(R).")]
    [InlineData("bs_thrown(R).")]
    [InlineData("bs_scryer(R).")]
    public void TheModuleWritesAsTier0Does(string goal)
        => Assert.Equal(Tier0(goal), Run(goal).Answer);

    [DiagFact]
    public void ALoopOfWritesStaysInTheModule()
    {
        var run = Run("bs_loop(1000).");
        Assert.Equal("", run.Answer);
        // The first write of the query, then one each time the ring or the
        // trail's headroom fills: a few, against a thousand.
        Assert.True(run.Requests <= 20, $"{run.Requests} of 1001 writes left the module");
    }
}
