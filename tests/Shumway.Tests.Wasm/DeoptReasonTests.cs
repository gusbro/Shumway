using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Every step-aside names its reason in the histogram the meta-call
/// guards feed. Half of clpr's browser deopts read as ANONYMOUS because
/// sites were stamped one at a time as each was suspected; EmitDeopt now
/// requires the reason in its signature, so compiling at all proves no site
/// was left out. These pin the codes for causes a corpus can force
/// deterministically.</summary>
public sealed class DeoptReasonTests(ITestOutputHelper o)
{
    private const int AttvarBind = 24;
    private const int Arithmetic = 26;

    private long[] RunAndReadHistogram(string corpus, string warm, string query)
    {
        var (engine, _) = TieredEngine.Build(corpus);
        Assert.True(engine.Query(warm).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query(query).Success);
        for (int g = 0; g < WasmTierDelegate.DiagMetaGuardHist.Length; g++)
            if (WasmTierDelegate.DiagMetaGuardHist[g] != 0)
                o.WriteLine($"  {WasmTierDelegate.DiagMetaGuardHist[g]} guard {g}: "
                    + Shumway.Compiler.Wasm.WasmPredicateCompiler.DeoptReasonName(g));
        return WasmTierDelegate.DiagMetaGuardHist;
    }

    /// <summary>An attributed variable met by a STRUCTURE head match steps
    /// aside for the wakeup, and says so. Through get_value it lands in the
    /// general unifier instead, whose code covers attvars among its shapes,
    /// so the corpus goes through the head match on purpose.</summary>
    [DiagFact]
    public void BindingAnAttvarNamesItsReason()
    {
        var hist = RunAndReadHistogram("""
            p(f(_)).
            bindall([]).
            bindall([X|Xs]) :- p(X), bindall(Xs).
            mk([], []).
            mk([X|Xs], [Y|Ys]) :- put_attr(X, m, Y), mk(Xs, Ys).
            run(N) :- length(L, N), mk(L, _), bindall(L).
            """,
            "run(2).", "run(20).");
        Assert.True(WasmTierDelegate.DiagDeopts >= 20,
            $"the corpus stopped deopting ({WasmTierDelegate.DiagDeopts}): it "
            + "no longer exercises the reason it pins");
        Assert.True(hist[AttvarBind] >= 20,
            $"{hist[AttvarBind]} at code {AttvarBind}: the attvar bind "
            + "stepped aside without naming itself");
    }

    /// <summary>A bignum result escalates to the host's evaluator, and says
    /// so.</summary>
    [DiagFact]
    public void BignumArithmeticNamesItsReason()
    {
        // An int multiply whose result leaves the 60-bit cell: the inline
        // evaluator escalates to the host mid-expression. 2 ** 100 will not
        // do -- ** is a builtin request, not an inline operator. The warm
        // run must NOT reach the overflow: a site that deopted while warm
        // is learned from, and the measured run then never enters the
        // module at all -- observed as hist[26] = 1 with DiagDeopts = 0.
        // The warm call runs over/2 once on Tier-0 with a SMALL operand,
        // which is what promotes it without teaching anyone about the
        // overflow; and the operand arrives in a variable because two
        // literal operands are folded at compile time, leaving no runtime
        // arithmetic to step aside. The overflow's first wasm execution is
        // the measured one.
        var hist = RunAndReadHistogram("""
            loop(0).
            loop(N) :- N > 0, N1 is N - 1, loop(N1).
            over(A, X) :- X is A * A.
            """,
            "loop(2), over(3, _).", "over(1000000000, X), X > 0.");
        Assert.True(WasmTierDelegate.DiagDeopts >= 1,
            "the corpus stopped deopting: it no longer exercises the reason");
        Assert.True(hist[Arithmetic] >= 1,
            $"{hist[Arithmetic]} at code {Arithmetic}: the bignum escalation "
            + "stepped aside without naming itself");
    }

    /// <summary>Zero stays reserved: a module THIS compiler emits under
    /// DebugMetaGuards never reads as anonymous, so a zero in a report can
    /// only mean a module compiled without stamps.</summary>
    [DiagFact]
    public void NothingThisCompilerEmitsIsAnonymous()
    {
        var hist = RunAndReadHistogram("""
            bindall([]).
            bindall([X|Xs]) :- eq(X, 1), bindall(Xs).
            eq(V, V).
            mk([], []).
            mk([X|Xs], [Y|Ys]) :- put_attr(X, m, Y), mk(Xs, Ys).
            run(N) :- length(L, N), mk(L, _), bindall(L),
                      _ is 1000000000 * 1000000000.
            """,
            "run(2).", "run(20).");
        Assert.True(WasmTierDelegate.DiagDeopts > 0, "nothing stepped aside");
        Assert.Equal(0L, hist[0]);
    }
}
