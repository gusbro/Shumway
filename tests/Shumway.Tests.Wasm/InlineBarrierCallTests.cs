using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>'$call'/2 -- the meta-call that CARRIES its cut barrier -- and the
/// cut it most often carries.
///
/// <para>The body conversion of SS7.6.2 takes a conjunction apart element by
/// element: `( a, ! ; b )` reaches the prelude's helper as
/// `'$call_conj'(a, !, K)`, so the `!` arrives ALONE, with K. A bare cut with
/// no barrier would be meaningless -- it would prune an empty block -- and the
/// carried barrier is exactly what gives it meaning: it cuts as far as the
/// call that established it, and no further.</para>
///
/// <para>Measured, that is a third of clpr's remaining deopts. The module can
/// do it -- it already has a cut -- and the only thing it cannot do is fire a
/// setup_call_cleanup handler, so a live one makes it decline.</para>
///
/// <para>Every test here counts SOLUTIONS on both sides of the cut. A cut that
/// prunes too much or too little still answers "yes" to the first question,
/// which is why asking only that would prove nothing.</para></summary>
public sealed class InlineBarrierCallTests(ITestOutputHelper o)
{
    private const string Corpus = """
        a(1).
        a(2).
        b(3).
        b(4).
        conj(X)  :- ( a(X), ! ; b(X) ).
        noCut(X) :- ( a(X) ; b(X) ).
        deep(X)  :- ( a(X), ( X > 1, ! ; true ) ; b(X) ).
        arrow(X) :- ( a(X) -> true ; X = none ).
        outer(X, Y) :- a(X), conj(Y).
        """;

    [Theory]
    // The cut commits to the first a/1 and drops both the rest of a/1 AND
    // the disjunction's second branch.
    [InlineData("conj(X)", "[1]")]
    // The control: without the cut, all four.
    [InlineData("noCut(X)", "[1,2,3,4]")]
    // A cut nested one level deeper still reaches only its own call.
    [InlineData("deep(X)", "[1,2]")]
    [InlineData("arrow(X)", "[1]")]
    // And the cut inside conj/1 must NOT prune the caller's choice points:
    // outer/2 still has two solutions for X.
    [InlineData("outer(X, _)", "[1,2]")]
    public void TheCutReachesExactlyAsFarAsItsCall(string goal, string _)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, _2) = TieredEngine.Build(Corpus);

        string q = $"findall(X, {goal}, L), with_output_to(atom(A), writeq(L)).";
        string want = plain.Query(q).Bindings["A"].ToString()!;
        string got = tiered.Query(q).Bindings["A"].ToString()!;
        o.WriteLine($"{goal} -> {want}");
        Assert.Equal(want, got);
    }

    /// <summary>A live setup_call_cleanup handler makes a cut FIRE it. The
    /// module cannot run a cleanup -- that is meta-calling a goal from inside
    /// the cut -- so it declines, and the cleanup must still run exactly
    /// once.</summary>
    [Fact]
    public void ACutThatFiresACleanupStillRunsIt()
    {
        const string P = """
            :- dynamic(fired/1).
            a(1).
            a(2).
            guarded(X) :- setup_call_cleanup(true, ( a(X), ! ), assertz(fired(yes))).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(P);
        Assert.True(plain.Query("findall(X, guarded(X), L), L == [1].").Success,
            "the interpreter's own answer moved");
        Assert.True(plain.Query("findall(_, fired(_), F), length(F, 1).").Success,
            "the interpreter fired the cleanup a different number of times");

        var (tiered, _) = TieredEngine.Build(P);
        Assert.True(tiered.Query("findall(X, guarded(X), L), L == [1].").Success,
            "the tiered answer moved");
        Assert.True(tiered.Query("findall(_, fired(_), F), length(F, 1).").Success,
            "the cleanup did not fire exactly once on the tier");
    }

    /// <summary>The counter: the carried cut stops leaving the module.</summary>
    [DiagFact]
    public void TheCarriedCutStopsSteppingAside()
    {
        var (engine, _) = TieredEngine.Build("""
            a(1).
            a(2).
            spin(0, []).
            spin(N, [X|R]) :- N > 0, atom_length(ab, _),
                              ( a(X), ! ; X = none ),
                              N1 is N - 1, spin(N1, R).
            """);
        // Warm: the meta cache and the markers are cold once by construction.
        Assert.True(engine.Query("spin(20, _).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("spin(20, L), length(L, 20).").Success);

        long lens = 0;
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            if (n == "atom_length" && a == 2) lens = hits;
        o.WriteLine($"deopts={WasmTierDelegate.DiagDeopts} atom_length/2={lens} "
            + $"hops={WasmTierDelegate.DiagInWasmHops}");
        Assert.True(lens >= 20, $"the clause never ran on the tier ({lens} exits)");
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
    }
}
