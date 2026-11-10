using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>findall/3's collect loop in the module. The buffer it opens and
/// each solution that is an atom or an integer are parked for the host in
/// order, and a collect whose whole call is still parked builds the list
/// itself, so a findall over a generator that runs in the module (clp(Z)'s
/// numlist/3, 3,011 calls in one sudoku solve) does not cross at all. A
/// compound solution is still the host's to copy, and every request drains
/// what was parked before it, so the order holds. Tier-0 is the oracle.
/// </summary>
public sealed class FindallInTheModuleTests(ITestOutputHelper o)
{
    // btw/3 and not between/3: the engine's between/3 is a builtin the
    // module requests, and a request drains the parked buffer to the host.
    private const string Corpus = """
        btw(L, H, L) :- L =< H.
        btw(L, H, X) :- L < H, L1 is L + 1, btw(L1, H, X).
        nums(N, L) :- findall(X, btw(1, N, X), L).
        nums_loop(0) :- !.
        nums_loop(K) :- nums(5, L), L = [1|_], K1 is K - 1, nums_loop(K1).
        shapes(L) :- findall(X, member(X, [1, f(a), b, g(c, d), 2]), L).
        compounds(L) :- findall(p(X), btw(1, 3, X), L).
        nested(L) :- findall(Y-L1, (member(Y, [1, 2]), findall(Z, btw(Y, 3, Z), L1)), L).
        none(L) :- findall(X, fail, L).
        given(R) :-
            ( findall(X, btw(1, 2, X), [1, 3]) -> R0 = bad ; R0 = ok ),
            findall(Y, member(Y, [a]), L2), R = R0-L2.
        thrown(R) :-
            catch(findall(X, (btw(1, 3, X), X > a), _), error(E, _), R0 = E),
            findall(Y, btw(1, 2, Y), L), R = R0-L.
        """;

    private (string Answer, Dictionary<string, long> Builtins, long Foreign) Run(string goal)
    {
        var (engine, members) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query(goal).Success, goal);      // warm: promote
        Assert.NotEmpty(members);
        WasmTierDelegate.ResetDiag();
        string answer = Answer(engine.Query(goal));
        long foreign = WasmTierDelegate.DiagForeignExits;
        var builtins = new Dictionary<string, long>();
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            builtins[$"{name}/{arity}"] = hits;
        o.WriteLine($"{goal}: {answer} deopts={WasmTierDelegate.DiagDeopts} foreign={foreign} builtins="
            + string.Join(" ", builtins.Select(b => $"{b.Key}:{b.Value}")));
        // Every buffer a call opened is closed again.
        Assert.True(engine.Query("findall(Q, member(Q, [z]), [z]).").Success,
            "a later findall found a buffer left open");
        return (answer, builtins, foreign);
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
    [InlineData("nums(4, L).")]
    [InlineData("shapes(L).")]
    [InlineData("compounds(L).")]
    [InlineData("nested(L).")]
    [InlineData("none(L).")]
    [InlineData("given(R).")]
    [InlineData("thrown(R).")]
    public void TheModuleCollectsAsTier0Does(string goal)
        => Assert.Equal(Tier0(goal), Run(goal).Answer);

    [DiagFact]
    public void AFindallOfIntegersDoesNotCross()
    {
        var run = Run("nums_loop(50).");
        Assert.Equal("", run.Answer);
        // In the module at all: a collect loop refused by the crossing census
        // runs on Tier-0 and requests nothing either, but every call to it
        // closes the chain.
        Assert.Equal(0L, run.Foreign);
        foreach (var name in new[] { "$findall_push/0", "$findall_record_s/1", "$findall_collect/1" })
            Assert.False(run.Builtins.ContainsKey(name), $"{name} left the module");
    }

    /// <summary>A compound solution is copied by the host: the anti-vacuity
    /// check that the request is still there, and that the order across the
    /// two ways holds (shapes/1 interleaves them).</summary>
    [DiagFact]
    public void ACompoundSolutionIsStillTheHosts()
    {
        var run = Run("compounds(L).");
        Assert.Equal("L=.(p(1), .(p(2), .(p(3), [])))", run.Answer);
        Assert.True(run.Builtins.ContainsKey("$findall_record_s/1"));
    }
}
