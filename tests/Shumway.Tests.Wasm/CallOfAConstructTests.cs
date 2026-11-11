using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>call/1 of a control construct, from compiled code. The host
/// converts the goal as a body first (ISO 7.6.2, 7.8.3) and hands the
/// construct to its prelude helper with B as the barrier; the module does
/// the same when the conversion would leave the goal as it is, and leaves
/// the rest to the host. Tier-0 is the oracle; the counters say which of the
/// two ran it.</summary>
public sealed class CallOfAConstructTests(ITestOutputHelper o)
{
    private const string Corpus = """
        a(1).
        a(2).
        b(1).
        b(2).
        c(2).
        % Built at run time, so the compiler cannot rewrite the call/1.
        mk(F, A, B, G) :- G =.. [F, A, B].

        p_conj(X-Y) :- mk(',', a(X), b(Y), G), call(G).
        p_disj(X) :- mk(';', a(X), c(X), G), call(G).
        p_spine(X-Y) :- mk(',', a(X), (b(Y), c(Y)), G), call(G).
        % The right goal reached through a REF, which the host normalizes.
        p_spine_ref(X-Y) :- mk(',', b(Y), c(Y), R), mk(',', a(X), R, G), call(G).
        p_tagged(X-Y) :- mk(',', a(X), b(Y), G), mk(':', user, G, Q), call(Q).
        % The cut is call/1's: it commits a(X) and no further.
        p_cut(X) :- mk(',', a(X), !, G), call(G).
        p_cut(9).
        % An if-then-else: a construct on the left of the ';'.
        p_ite(X) :- mk('->', a(X), true, I), mk(';', I, c(X), G), call(G).
        % 7.6.2: Z is a variable when call/1 converts, so it becomes call(Z),
        % and the `!` it is bound to cuts only inside that call.
        p_var(X) :- mk(',', member(X, [1, 2]), Z, R), mk(',', Z = !, R, G), call(G).
        % 7.8.3: the whole conjunction is the culprit, and its first goal
        % never runs.
        p_bad(T, R) :-
            nb_setval(ran, no),
            catch(bad_call, error(type_error(callable, T), _), true),
            nb_getval(ran, R).
        bad_call :- mk(',', nb_setval(ran, yes), 1, G), call(G).

        all(G, L) :- findall(T, call(G, T), L).
        """;

    private (string Answer, long Deopts, long Declined, long Builtins, long Entries,
        Dictionary<string, long> Sites) Run(string goal)
    {
        var (engine, members) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query(goal).Success, goal);      // warm: promote
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
        long declined = WasmTierDelegate.DiagMetaGuardHist[37];
        o.WriteLine($"{goal}: {answer} deopts={WasmTierDelegate.DiagDeopts} guard37={declined} "
            + $"builtins={WasmTierDelegate.DiagBuiltins} entries={WasmTierDelegate.DiagEntries} "
            + string.Join(" ", Enumerable.Range(0, 64).Where(g => WasmTierDelegate.DiagMetaGuardHist[g] > 0)
                .Select(g => $"g{g}={WasmTierDelegate.DiagMetaGuardHist[g]}")) + " "
            + string.Join(" ", sites.Select(s => $"{s.Key}:{s.Value}")));
        return (answer, WasmTierDelegate.DiagDeopts, declined, WasmTierDelegate.DiagBuiltins,
            WasmTierDelegate.DiagEntries, sites);
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

    [DiagTheory]
    [InlineData("p_conj", "L=.(-(1, 1), .(-(1, 2), .(-(2, 1), .(-(2, 2), []))))")]
    [InlineData("p_disj", "L=.(1, .(2, .(2, [])))")]
    [InlineData("p_spine", "L=.(-(1, 2), .(-(2, 2), []))")]
    [InlineData("p_tagged", "L=.(-(1, 1), .(-(1, 2), .(-(2, 1), .(-(2, 2), []))))")]
    [InlineData("p_cut", "L=.(1, .(9, []))")]
    public void AConstructTheConversionLeavesAloneRunsFromTheModule(string pred, string expected)
    {
        string goal = $"all({pred}, L).";
        Assert.Equal(expected, Tier0(goal));
        var run = Run(goal);
        Assert.Equal(expected, run.Answer);
        Assert.False(run.Sites.ContainsKey($"user${pred}/1"), $"call/1 in {pred}/1 left the module");
        Assert.Equal(0L, run.Declined);
    }

    [DiagTheory]
    [InlineData("all(p_ite, L).", "L=.(1, [])")]
    [InlineData("all(p_var, L).", "L=.(1, .(2, []))")]
    [InlineData("all(p_spine_ref, L).", "L=.(-(1, 2), .(-(2, 2), []))")]
    [InlineData("p_bad(T, R).", "T=,(nb_setval(ran, yes), 1),R=no")]
    public void AConstructTheHostConvertsIsTheHosts(string goal, string expected)
    {
        Assert.Equal(expected, Tier0(goal));
        var run = Run(goal);
        Assert.Equal(expected, run.Answer);
        Assert.True(run.Declined > 0, "the module did not hand the construct to the host");
    }
}
