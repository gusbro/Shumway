using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>An inline =/2 that meets a pair only the engine's unifier can
/// decide leaves as a LEAF BUILTIN REQUEST, chain open, instead of a deopt
/// that closes the chain and leaves the rest of the clause to the
/// interpreter. When the unify binds an attributed variable it queues a
/// wakeup, and a wakeup must fire at the next goal boundary -- boundaries
/// inside the chain read a Flags word staged at entry, so the delegate
/// closes the chain right after such a builtin and the interpreter, whose
/// boundaries read live state, carries on. One exit per wakeup, same count
/// as the deopt it replaces, none of the staging.</summary>
public sealed class CheapUnifyEscapeTests(ITestOutputHelper o)
{
    /// <summary>THE ordering contract (ADR-049): the woken goal runs before
    /// the next user goal. The clause binds a frozen variable through =/2
    /// and the very next goal looks at what the hook was to have done --
    /// a chain that sailed past the boundary would see the variable still
    /// unbound and fail.</summary>
    [Fact]
    public void AWakeQueuedInsideTheChainFiresAtTheNextBoundary()
    {
        const string Corpus = """
            :- use_module(library(coroutining)).
            seen(S) :- nonvar(S).
            probe(X, S) :- freeze(X, S = woke), bindit(X), seen(S).
            bindit(V) :- V = bound.
            """;
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query("probe(_, S), S == woke.").Success,
            "the interpreter's own ordering moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("probe(_, S), S == woke.").Success,
            "the woken goal did not run before the next user goal");
    }

    /// <summary>The counter: clpr's solve loop, which used to deopt once per
    /// wake, now leaves only as builtin requests -- and still leaves, which
    /// is the anti-vacuity: =/2 shows in the builtin ranking with the count
    /// the deopts used to have.</summary>
    [DiagFact]
    public void TheAttvarBindLeavesAsABuiltinRequestNotADeopt()
    {
        var (engine, _) = TieredEngine.Build("""
            :- use_module(library(clpr)).
            csolve(0) :- !.
            csolve(N) :- {X + Y =:= 10, X - Y =:= 2}, X =:= 6.0, Y =:= 4.0,
                         M is N - 1, csolve(M).
            """);
        Assert.True(engine.Query("csolve(3).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("csolve(10).").Success);

        long eq = 0;
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            if (n == "=" && a == 2) eq = hits;
        o.WriteLine($"deopts={WasmTierDelegate.DiagDeopts} =/2 exits={eq}");
        Assert.True(eq >= 10,
            $"=/2 shows {eq} exits: the escape is not running, so either the "
            + "corpus stopped binding attvars or the deopt is back");
        // The property this pins is that an ATTVAR BIND does not deopt,
        // and the count of ALL deopts stopped being the way to say it: a
        // cut with something on the extra trail now steps aside on purpose
        // (guard 28), because the compaction it owes is the engine's. So
        // the assertion names the reason instead of counting reasons.
        for (int g = 0; g < WasmTierDelegate.DiagMetaGuardHist.Length; g++)
        {
            if (g == 28 || WasmTierDelegate.DiagMetaGuardHist[g] == 0) continue;
            Assert.Fail($"{WasmTierDelegate.DiagMetaGuardHist[g]} deopts at "
                + $"guard {g}: "
                + Shumway.Compiler.Wasm.WasmPredicateCompiler.DeoptReasonName(g));
        }
    }
}
