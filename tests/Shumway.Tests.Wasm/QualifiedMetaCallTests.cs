using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Three meta-calls the module resolved only on the host, all of
/// them clp(Z)'s: a closure a meta_predicate declaration qualified as
/// <c>Module:Goal</c>, a goal whose predicate takes meta-arguments, and a
/// control construct that reaches <c>'$call'/2</c>. The host dispatcher
/// unwraps <c>M:G</c> as it unwraps the engine's own tag, qualifies the
/// callee's meta-arguments, and hands a construct to its prelude helper; the
/// module now does the same, and Tier-0 is the oracle. Counted, not timed:
/// the second run of each goal leaves the module at no meta-call.</summary>
public sealed class QualifiedMetaCallTests(ITestOutputHelper o)
{
    private const string Corpus = """
        inc(X, Y) :- Y is X + 1.
        mapq(_, [], []).
        mapq(G, [X|Xs], [Y|Ys]) :- call(G, X, Y), mapq(G, Xs, Ys).
        colon_run(L) :- mapq(user:inc, [1, 2, 3], L).
        :- meta_predicate(probe_q(2, -)).
        probe_q(G, S) :- ( G = M:_ -> S = q(M) ; S = bare ).
        metaarg_run(G, S) :- call(G, S).
        a(1).
        a(2).
        b(1).
        b(2).
        c(2).
        % Built at run time, so the compiler cannot rewrite the call/1.
        conj(B, X, G) :- G =.. [',', a(X), B].
        flat_run(L) :- conj(b(Y), X, G), findall(X-Y, call(G), L).
        nested_run(L) :- B =.. [',', b(Y), c(Y)], conj(B, X, G), findall(X-Y, call(G), L).
        disj_run(L) :- B =.. [';', b(Y), c(Y)], conj(B, X, G), findall(X-Y, call(G), L).
        cut_run(L) :- B =.. [',', b(Y), !], conj(B, X, G), findall(X-Y, call(G), L).
        ifthen_run(L) :-
            I =.. ['->', b(Y), true], B =.. [';', I, c(Y)], conj(B, X, G),
            findall(X-Y, call(G), L).
        """;

    private (string Answer, long Guard9, long Deopts, Dictionary<string, long> Sites)
        Run(string goal)
    {
        var (engine, members) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query(goal).Success, goal);      // warm: promote, fill the cache
        Assert.NotEmpty(members);
        WasmTierDelegate.ResetDiag();
        var r = engine.Query(goal);
        string answer = Answer(r);
        var sites = new Dictionary<string, long>();
        foreach (var (pc, hits) in WasmTierDelegate.DeoptRanking())
        {
            string name = NameAt(engine, pc);
            sites[name] = sites.GetValueOrDefault(name) + hits;
        }
        long guard9 = WasmTierDelegate.DiagMetaGuardHist[9];
        o.WriteLine($"{goal}: {answer} deopts={WasmTierDelegate.DiagDeopts} guard9={guard9} "
            + string.Join(" ", sites.Select(s => $"{s.Key}:{s.Value}")));
        return (answer, guard9, WasmTierDelegate.DiagDeopts, sites);
    }

    private static string NameAt(PrologEngine engine, long pc)
    {
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
            if (pc >= addr && pc < addr + pred.Bytecode.Length)
            {
                var (aid, ar) = Shumway.Core.FunctorTable.Lookup(pred.FunctorId);
                return $"{Shumway.Core.AtomTable.GetById(aid)?.Name}/{ar}";
            }
        return "?";
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

    [DiagFact]
    public void AColonQualifiedClosureHitsTheCache()
    {
        const string goal = "colon_run(L).";
        var run = Run(goal);
        Assert.Equal(Tier0(goal), run.Answer);
        Assert.Equal("L=.(2, .(3, .(4, [])))", run.Answer);
        Assert.Equal(0L, run.Guard9);
    }

    /// <summary>probe_q/2 reports whether its closure arrived qualified: the
    /// host qualifies it, so a module that jumped from the cache without
    /// qualifying would answer <c>bare</c>.</summary>
    [DiagFact]
    public void AGoalWithMetaArgumentsTakesThemQualified()
    {
        const string goal = "metaarg_run(probe_q(inc), S).";
        var run = Run(goal);
        Assert.Equal("S=q(user)", Tier0(goal));
        Assert.Equal(Tier0(goal), run.Answer);
        Assert.Equal(0L, run.Guard9);
    }

    /// <summary>'$call_conj'/3 hands each piece to '$call'/2 with the
    /// call's barrier, and a barrier that is an integer is the module's to
    /// take: the check that tells one apart sent every piece to the host.
    /// </summary>
    [DiagFact]
    public void AConjunctionsPiecesRunFromTheModule()
    {
        const string goal = "flat_run(L).";
        var run = Run(goal);
        Assert.Equal(Tier0(goal), run.Answer);
        Assert.False(run.Sites.ContainsKey("$call_conj/3"),
            "a piece of the conjunction left '$call_conj'/3");
    }
}
