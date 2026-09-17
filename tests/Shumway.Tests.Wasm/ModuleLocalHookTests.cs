using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The constraint libraries' verify_attributes hooks are
/// MODULE-LOCAL (ADR-040), not the legacy shared multifile predicate --
/// and that is a tier property, not a style choice. A multifile predicate
/// is registered as dynamic so clauses can accumulate across consults, a
/// dynamic predicate stays off the tier by invariant, and so the wake
/// driver's meta-call to the hook deopted on every wakeup: measured in the
/// browser, half of clpr's and clpfd's deopts were exactly this. A
/// module-local hook is an ordinary static predicate the engine resolves
/// first (Verify4FunctorId), with a call marker the meta-call can jump
/// through.</summary>
public sealed class ModuleLocalHookTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(clpr)).
        csolve(0) :- !.
        csolve(N) :- {X + Y =:= 10, X - Y =:= 2}, X =:= 6.0, Y =:= 4.0,
                     M is N - 1, csolve(M).
        """;

    /// <summary>The counter: waking clpr's hook no longer declines for want
    /// of a marker. Guard 4 with [last: verify_attributes/4] is exactly what
    /// a multifile (hence dynamic, hence unpromotable) hook reports, and
    /// re-adding the multifile directive turns this red with that exact
    /// signature.</summary>
    [DiagFact]
    public void WakingTheHookNoLongerDeclinesForWantOfAMarker()
    {
        var (engine, _, _) = TieredEngine.BuildWithWorld(Corpus);
        Assert.True(engine.Query("csolve(3).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("csolve(10).").Success);

        o.WriteLine($"deopts={WasmTierDelegate.DiagDeopts} "
            + $"guard4={WasmTierDelegate.DiagMetaGuardHist[4]}");
        // Anti-vacuity: the wakeup path still runs and still steps aside for
        // the attvar bind itself (guard 25) -- that one is the wakeup, not a
        // resolution failure, and it is the proof the corpus still wakes.
        Assert.True(WasmTierDelegate.DiagMetaGuardHist[25] >= 10,
            "the corpus stopped waking hooks: it no longer exercises the path");
        Assert.Equal(0L, WasmTierDelegate.DiagMetaGuardHist[4]);
    }

    /// <summary>And the answers did not move: the interpreter is the oracle,
    /// on a query whose value checks (=:=) fail if the hook stopped running
    /// or ran with different bindings.</summary>
    [Fact]
    public void TheHookStillRunsAndAgreesWithTheInterpreter()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, _) = TieredEngine.Build(Corpus);
        foreach (string q in new[]
        {
            "csolve(5).",
            "{A + B =:= 10, A - B =:= 2}, A =:= 6.0, B =:= 4.0.",
            // Not `C = 0.0`: binding a variable that carries only
            // INEQUALITIES is accepted unchecked today -- the hook returns
            // residual goals only for dep(...) attributes -- so that query
            // succeeds in BOTH engines and pins nothing. Posting the value
            // as a constraint takes the path that does decide.
            "\\+ ( {C >= 1.0}, {C =:= 0.0} ).",
        })
        {
            bool expected = plain.Query(q).Success;
            Assert.True(expected, $"the oracle itself fails: {q}");
            Assert.Equal(expected, tiered.Query(q).Success);
        }
    }
}
