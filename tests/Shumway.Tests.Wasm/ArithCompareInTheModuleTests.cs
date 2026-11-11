using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>An arithmetic comparison reached through a meta-call: a
/// <c>call(=&lt;, A, B)</c> closure, or a goal built at run time and handed to
/// call/1. A clause body never reaches the builtin (the compiler lowers the
/// comparison), but these did, and each one crossed to the host. Two small
/// integers are compared in the module; every other shape is the host's to
/// evaluate or to raise on. Tier-0 is the oracle.</summary>
public sealed class ArithCompareInTheModuleTests(ITestOutputHelper o)
{
    private const string Corpus = """
        num(-576460752303423488).
        num(-3).
        num(0).
        num(2).
        num(7).
        num(576460752303423487).
        cmp(R, A, B) :- call(R, A, B).
        % Built at run time, so the compiler cannot lower it.
        cmpg(R, A, B) :- G =.. [R, A, B], call(G).
        truth(R, L) :- findall(A-B, (num(A), num(B), cmp(R, A, B)), L).
        truthg(R, L) :- findall(A-B, (num(A), num(B), cmpg(R, A, B)), L).
        loop(_, _, _, 0) :- !.
        loop(R, A, B, N) :- cmp(R, A, B), N1 is N - 1, loop(R, A, B, N1).
        loopg(_, _, _, 0) :- !.
        loopg(R, A, B, N) :- cmpg(R, A, B), N1 is N - 1, loopg(R, A, B, N1).
        err(R, A, B, E) :- catch(cmp(R, A, B), error(E, _), true).
        yes(R, A, B, T) :- ( cmp(R, A, B) -> T = yes ; T = no ).
        rat(T) :- Q is 1 rdiv 3, yes(=<, Q, 1, T).
        """;

    private (string Answer, Dictionary<string, long> Builtins, long Deopts) Run(string goal)
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
        return (answer, builtins, WasmTierDelegate.DiagDeopts);
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
    [InlineData(@"truth(=:=, L).")]
    [InlineData(@"truth(=\=, L).")]
    [InlineData(@"truth(<, L).")]
    [InlineData(@"truth(>, L).")]
    [InlineData(@"truth(=<, L).")]
    [InlineData(@"truth(>=, L).")]
    [InlineData(@"truthg(<, L).")]
    [InlineData(@"truthg(>=, L).")]
    [InlineData(@"yes(<, 1, 2.5, T).")]
    [InlineData(@"yes(=:=, 1, 1.0, T).")]
    [InlineData(@"yes(<, 1 + 1, 3, T).")]
    [InlineData(@"yes(>, 123456789012345678901234567890, 7, T).")]
    [InlineData(@"rat(T).")]
    [InlineData(@"err(<, _, 1, E).")]
    [InlineData(@"err(<, a, 1, E).")]
    [InlineData(@"err(=:=, 1, foo(2), E).")]
    public void TheModuleAnswersAsTier0Does(string goal)
    {
        string expected = Tier0(goal);
        Assert.NotEqual("fail", expected);
        Assert.Equal(expected, Run(goal).Answer);
    }

    [DiagTheory]
    [InlineData("loop", @"=:=", "2", "2")]
    [InlineData("loop", @"=\=", "1", "2")]
    [InlineData("loop", @"<", "-3", "2")]
    [InlineData("loop", @">", "7", "-3")]
    [InlineData("loop", @"=<", "2", "2")]
    [InlineData("loop", @">=", "576460752303423487", "-576460752303423488")]
    [InlineData("loopg", @"=:=", "2", "2")]
    [InlineData("loopg", @"=\=", "1", "2")]
    [InlineData("loopg", @"<", "-3", "2")]
    [InlineData("loopg", @">", "7", "-3")]
    [InlineData("loopg", @"=<", "2", "2")]
    [InlineData("loopg", @">=", "576460752303423487", "-576460752303423488")]
    public void ComparingTwoSmallIntegersStaysInTheModule(string loop, string rel, string a, string b)
    {
        var run = Run($"{loop}({rel}, {a}, {b}, 200).");
        Assert.Equal("", run.Answer);
        Assert.Equal(0L, run.Deopts);
        Assert.False(run.Builtins.ContainsKey($"{rel}/2"), $"{rel}/2 left the module from {loop}/4");
    }

    /// <summary>A float is the host's to compare: the anti-vacuity check
    /// that the request is still there and the loop does reach the builtin.
    /// </summary>
    [DiagFact]
    public void AFloatIsStillTheHosts()
    {
        var run = Run("loop(<, 1, 2.5, 50).");
        Assert.Equal("", run.Answer);
        Assert.True(run.Builtins.GetValueOrDefault("</2") >= 50, "the float comparison did not reach the host");
    }
}
