using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>compare/3 over the module's own standard-order comparator,
/// called or reached through a meta-call. library(assoc) asks compare/3 once
/// per tree level, and it crossed to the host every time. Tier-0 is the oracle; a pair the
/// comparator hands back (two different atoms) still crosses, and an Order
/// that is not an order atom is still the host's error.</summary>
public sealed class StandardOrderInTheModuleTests(ITestOutputHelper o)
{
    private const string Corpus = """
        mapq(_, [], []).
        mapq(G, [X|Xs], [Y|Ys]) :- call(G, X, Y), mapq(G, Xs, Ys).
        pair(1, 2).
        pair(2, 1).
        pair(3, 3).
        pair(f(1, b), f(1, c)).
        pair(g(2), f(1, 1)).
        pair(1, a).
        pair(a, a).
        orders(L) :- findall(O, (pair(A, B), compare(O, A, B)), L).
        given(L) :- findall(A-B, (pair(A, B), compare(<, A, B)), L).
        atoms(O) :- compare(O, apple, banana).
        bad(E) :- catch(cmp_bad, error(E, _), true).
        cmp_bad :- compare(foo, 1, 2).
        cmp_loop(0) :- !.
        cmp_loop(N) :- compare(<, N, 1000), N1 is N - 1, cmp_loop(N1).
        meta_cmp(R) :- ( mapq(compare(<), [1, 2], [2, 3]) -> R = yes ; R = no ).
        meta_cmp_no(R) :- ( mapq(compare(<), [1, 3], [2, 3]) -> R = yes ; R = no ).
        meta_arg(L) :- mapq(arg(1), [f(a), g(b, c), h(d)], L).
        """;

    private (string Answer, Dictionary<string, long> Builtins) Run(string goal)
    {
        var (engine, members) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query(goal).Success, goal);      // warm: promote
        Assert.NotEmpty(members);
        WasmTierDelegate.ResetDiag();
        string answer = Answer(engine.Query(goal));
        var builtins = new Dictionary<string, long>();
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            builtins[$"{name}/{arity}"] = hits;
        o.WriteLine($"{goal}: {answer} deopts={WasmTierDelegate.DiagDeopts} builtins="
            + string.Join(" ", builtins.Select(b => $"{b.Key}:{b.Value}")));
        return (answer, builtins);
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
    [InlineData("orders(L).")]
    [InlineData("given(L).")]
    [InlineData("atoms(O).")]
    [InlineData("bad(E).")]
    [InlineData("meta_cmp(R).")]
    [InlineData("meta_cmp_no(R).")]
    public void TheModuleAnswersAsTier0Does(string goal)
        => Assert.Equal(Tier0(goal), Run(goal).Answer);

    [DiagFact]
    public void ComparingNumbersStaysInTheModule()
    {
        var run = Run("cmp_loop(200).");
        Assert.Equal("", run.Answer);
        Assert.False(run.Builtins.ContainsKey("compare/3"), "compare/3 left the module");
    }

    /// <summary>Two different atoms are ordered by name, which only the host
    /// can read: the anti-vacuity check that the request is still there.
    /// </summary>
    [DiagFact]
    public void TwoAtomsAreStillTheHosts()
    {
        var run = Run("atoms(O).");
        Assert.Equal("O=<", run.Answer);
        Assert.True(run.Builtins.ContainsKey("compare/3"), "two atoms were ordered in the module");
    }

    [DiagTheory]
    [InlineData("meta_cmp(R).", "compare/3")]
    public void AMetaCalledBuiltinStaysInTheModule(string goal, string builtin)
    {
        var run = Run(goal);
        Assert.Equal(Tier0(goal), run.Answer);
        Assert.False(run.Builtins.ContainsKey(builtin), $"{builtin} left the module");
    }
}
