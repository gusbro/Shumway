using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A meta-called <c>true</c> carries on and a meta-called
/// <c>fail</c> fails, inside the module. As builtins they were requested
/// from the host every time: call(G) with G = true left the module once per
/// call, which made that loop slower on the tier than on Tier-0.</summary>
public sealed class MetaCallTrueFailTests(ITestOutputHelper o)
{
    private const string Corpus = """
        tloop(0, _) :- !.
        tloop(N, G) :- call(G), N1 is N - 1, tloop(N1, G).
        ttail(G) :- call(G).
        tframe(G, X, Y) :- call(G), X = 1, Y = X.
        tite(G, R) :- ( call(G) -> R = yes ; R = no ).
        fdriven(N, C) :- nb_setval(fc, 0),
            ( between(1, N, _), call(fail) ; true ),
            nb_getval(fc, C).
        fcount(N) :- nb_setval(fc, 0),
            ( between(1, N, _), nb_getval(fc, C0), C1 is C0 + 1, nb_setval(fc, C1),
              call(fail) ; true ).
        """;

    private static readonly string[] Goals =
    {
        "tloop(50, true).", "\\+ tloop(3, fail).", "ttail(true).", "\\+ ttail(fail).",
        "tframe(true, X, Y), X == 1, Y == 1.", "\\+ tframe(fail, _, _).",
        "tite(true, R), R == yes.", "tite(fail, R), R == no.",
        "fdriven(20, C), C == 0.", "fcount(20), nb_getval(fc, C), C == 20.",
        "G = true, call(G).", "G = fail, \\+ call(G).",
    };

    [Fact]
    public void TheAnswersAreTheInterpreters()
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);
        var (tiered, _) = TieredEngine.Build(Corpus);
        foreach (string goal in Goals)
        {
            Assert.True(plain.Query(goal).Success, goal);
            Assert.True(tiered.Query(goal).Success, goal + " under the module");
        }
    }

    private static long Exits(string name)
    {
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            if (n == name && a == 0) return hits;
        return 0;
    }

    [DiagFact]
    public void TrueAndFailStayInTheModule()
    {
        var (engine, _) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query("tloop(3, true).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("tloop(300, true).").Success);
        long t = Exits("true");
        o.WriteLine($"true exits={t} deopts={WasmTierDelegate.DiagDeopts}");
        Assert.True(t < 300, $"{t} exits for a meta-called true");

        Assert.True(engine.Query("fdriven(3, _).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("fdriven(300, _).").Success);
        long f = Exits("fail");
        o.WriteLine($"fail exits={f} deopts={WasmTierDelegate.DiagDeopts}");
        Assert.True(f < 300, $"{f} exits for a meta-called fail");
    }
}
