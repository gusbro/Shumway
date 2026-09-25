using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The module's unifier binds a plain unbound variable to an
/// attributed one by itself, the engine's own rule: the plain variable takes
/// Ref(the attvar's home), nothing wakes, so nothing needs the host. clp(Z)
/// does exactly this on every propagator step -- state(queue(_,_,_,Aux))
/// matched against the live queue, whose Aux carries attributes -- and it was
/// the single largest deopt in the browser (61% of them on queens).</summary>
public sealed class AttvarAgainstPlainVarTests(ITestOutputHelper o)
{
    // st//1 is clpz's state//1. The pattern's last slot is a plain variable
    // and the live state's is an attributed one; both sides are BOUND
    // compounds, so the pair reaches the general unifier and not the inline
    // =/2, which has its own escape.
    private const string Corpus = """
        :- use_module(library(coroutining)).
        st(S), [S] --> [S].
        mkq(queue(a, b, c, Aux)) :- put_attr(Aux, m, marked).
        step(Q, Aux) :- phrase(st(queue(_, _, _, Aux)), [Q], _).
        run(0, _) :- !.
        run(N, Q) :- step(Q, Aux), get_attr(Aux, m, marked), N1 is N - 1, run(N1, Q).
        go(N) :- mkq(Q), run(N, Q).
        direction(Q) :- \+ \+ step(Q, _), step(Q, Aux), var(Aux), get_attr(Aux, m, marked).
        mkf(queue(a, b, c, F)) :- freeze(F, true).
        stepv(Q) :- phrase(st(queue(_, _, _, x)), [Q], _).
        runv(0, _) :- !.
        runv(N, Q) :- \+ \+ stepv(Q), N1 is N - 1, runv(N1, Q).
        gov(N) :- mkf(Q), runv(N, Q).
        """;

    /// <summary>Which side binds is the whole question: bind the attributed
    /// variable to the plain one and its attributes are gone. After a step
    /// is undone by backtracking, a fresh step still finds the attribute on
    /// what it was bound to.</summary>
    [Fact]
    public void ThePlainVariableBindsAndTheAttributeSurvives()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query("go(50).").Success, "the interpreter's own rule moved");
        Assert.True(plain.Query("mkq(Q), direction(Q).").Success);

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("go(50).").Success, "the module bound the wrong side, or not at all");
        Assert.True(tiered.Query("mkq(Q), direction(Q).").Success,
            "after backtracking the attributed variable lost its attribute: the module bound it");
    }

    /// <summary>The counter: no unifier step-aside (reason 25) on a corpus
    /// that binds a plain variable to an attributed one on every iteration.
    /// The anti-vacuity is the same shape with a BOUND value in the slot,
    /// which wakes a hook and so must still step aside at that very site:
    /// the counter sees the site, and only the case that needs the host
    /// leaves.</summary>
    [DiagFact]
    public void BindingAPlainVariableToAnAttvarDoesNotStepAside()
    {
        var (engine, _) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query("go(5).").Success);
        Assert.True(engine.Query("gov(5).").Success);

        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("gov(200).").Success);
        long bound = WasmTierDelegate.DiagMetaGuardHist[25];
        o.WriteLine($"bound value in the slot: {bound} step-asides (reason 25), "
                    + $"deopts={WasmTierDelegate.DiagDeopts}");
        Assert.True(bound >= 200,
            $"{bound} step-asides for an attvar bound to a value: the corpus no "
            + "longer reaches the unifier through this site");

        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("go(200).").Success);
        for (int g = 0; g < WasmTierDelegate.DiagMetaGuardHist.Length; g++)
            if (WasmTierDelegate.DiagMetaGuardHist[g] != 0)
                o.WriteLine($"  {WasmTierDelegate.DiagMetaGuardHist[g]} guard {g}: "
                    + Shumway.Compiler.Wasm.WasmPredicateCompiler.DeoptReasonName(g));
        o.WriteLine($"plain variable in the slot: chains={WasmTierDelegate.DiagEntries} "
                    + $"deopts={WasmTierDelegate.DiagDeopts}");
        Assert.Equal(0, WasmTierDelegate.DiagMetaGuardHist[25]);
    }
}
