using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A retry/trust whose choice point saved a lower extra-trail top
/// than the live one cannot unwind it in the module: those entries restore
/// managed state. It asks the host to unwind both trails to the choice point
/// and runs again, instead of stepping aside and leaving the whole
/// alternative to the interpreter. One clause per kind of extra-trail entry,
/// each followed by a failure into the next clause of a module.</summary>
public sealed class RestoreUnwindsExtraTrailTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(coroutining)).
        attr_alt(X, 1) :- put_attr(X, m, 1), fail.
        attr_alt(X, 2) :- put_attr(X, m, 2), fail.
        attr_alt(X, 3) :- get_attr(X, m, V), V == 0.
        attrs :- put_attr(X, m, 0), attr_alt(X, R), R == 3.
        bset_alt(1) :- b_setval(rk, 1), fail.
        bset_alt(2) :- b_getval(rk, V), V == 0.
        bsets :- b_setval(rk, 0), bset_alt(R), R == 2.
        big_alt(X) :- X is 2 ** 200, fail.
        big_alt(small).
        bigs :- big_alt(X), X == small.
        catch_alt(1) :- catch(true, _, true), fail.
        catch_alt(2).
        catches :- catch_alt(R), R == 2, catch(throw(k), k, true).
        mixed_alt(X, Y, 1) :- X = a, put_attr(Y, m, 1), Y2 = Y, get_attr(Y2, m, 1), fail.
        mixed_alt(X, Y, 2) :- var(X), \+ get_attr(Y, m, _).
        mixed :- mixed_alt(_, _, R), R == 2.
        wake_alt(A, 1) :- A = b, fail.
        wake_alt(A, 2) :- var(A).
        wakes :- freeze(A, nb_setval(woke, yes)), nb_setval(woke, no),
                 wake_alt(A, R), R == 2, nb_getval(woke, yes).
        loop(0, _) :- !.
        loop(N, G) :- call(G), N1 is N - 1, loop(N1, G).
        direct(0) :- !.
        direct(N) :- attrs, bsets, mixed, N1 is N - 1, direct(N1).
        """;

    private static readonly string[] Goals =
        { "attrs.", "bsets.", "bigs.", "catches.", "mixed.", "wakes.",
          "loop(20, attrs).", "loop(20, bsets).", "loop(20, mixed).", "direct(20)." };

    [Fact]
    public void TheAnswersAreTheInterpreters()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, members) = TieredEngine.Build(Corpus);
        foreach (string goal in Goals)
        {
            Assert.True(plain.Query(goal).Success, goal);
            Assert.True(tiered.Query(goal).Success, goal + " under the module");
        }
        Assert.NotEmpty(members);
    }

    private static long Unwinds()
    {
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            if (n == "$unwind_cp_trails" && a == 0) return hits;
        return 0;
    }

    /// <summary>No restore steps aside for the extra trail (reason 23), and
    /// the request that replaced it runs: without it the first half holds
    /// trivially on a corpus that stopped reaching the site. Direct calls, not
    /// call/1: a meta-called atom goal steps aside for a reason of its own.
    /// </summary>
    [DiagFact]
    public void ARestoreAsksTheHostInsteadOfSteppingAside()
    {
        var (engine, _) = TieredEngine.Build(Corpus);
        foreach (string goal in Goals) Assert.True(engine.Query(goal).Success, goal);

        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("direct(100).").Success);
        long unwinds = Unwinds();
        o.WriteLine($"unwind requests={unwinds} guard23={WasmTierDelegate.DiagMetaGuardHist[23]} "
                    + $"deopts={WasmTierDelegate.DiagDeopts}");
        for (int g = 0; g < WasmTierDelegate.DiagMetaGuardHist.Length; g++)
            if (WasmTierDelegate.DiagMetaGuardHist[g] != 0)
                o.WriteLine($"  {WasmTierDelegate.DiagMetaGuardHist[g]} guard {g}: "
                    + Shumway.Compiler.Wasm.WasmPredicateCompiler.DeoptReasonName(g));
        Assert.Equal(0, WasmTierDelegate.DiagMetaGuardHist[23]);
        Assert.True(unwinds >= 300, $"{unwinds} unwind requests: the corpus no longer reaches the restore");
        Assert.Equal(0, WasmTierDelegate.DiagDeopts);
    }
}
