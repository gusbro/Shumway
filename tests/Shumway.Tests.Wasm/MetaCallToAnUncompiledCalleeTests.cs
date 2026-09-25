using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A meta-call whose callee nothing compiled.
///
/// <para>A meta-call and an ordinary call end on the same instruction: a
/// marker in the mailbox and a SuccessTailCall verdict. They differ only in
/// where the marker comes from, and the meta one reads it from a table that
/// was filled only when a module was installed. So a meta-call landing on a
/// predicate the tier never compiled -- one the census refused, a dynamic,
/// anything below the threshold -- read zero and STEPPED ASIDE, while a
/// direct call to that same predicate did not.</para>
///
/// <para>Measured on clp(Z) in a browser, that asymmetry was 160,888 of
/// 161,251 deopts in one goal.</para></summary>
public sealed class MetaCallToAnUncompiledCalleeTests(ITestOutputHelper o)
{
    /// <summary>A dynamic predicate is never promoted (it is the invariant,
    /// not an accident of thresholds), which makes it the callee that
    /// cannot be compiled no matter how hot the caller gets.</summary>
    private const string Corpus = """
        :- dynamic(target/2).
        target(a, 1).
        target(b, 2).
        target(c, 3).
        two(G, X, R) :- call(G, X, R).
        drive(L) :- findall(X-N, two(target, X, N), L).
        """;

    [DiagFact]
    public void AMetaCallToAnUncompiledPredicateDoesNotStepAside()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query("drive(L), L == [a-1, b-2, c-3].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        // The FIRST meta-call is the one that teaches the host, so a
        // measured run has to come after one. That is the design: the
        // marker is published on the path the host was taking anyway.
        Assert.True(tiered.Query("drive(_).").Success);
        WasmTierDelegate.ResetDiag();

        Assert.True(tiered.Query("drive(L), L == [a-1, b-2, c-3].").Success,
            "a meta-call to an uncompiled callee lost solutions");
        o.WriteLine($"deopts={WasmTierDelegate.DiagDeopts} "
            + $"tailExits={WasmTierDelegate.DiagTailExits} "
            + $"chains={WasmTierDelegate.DiagEntries}");
        foreach (var (pc, hits) in WasmTierDelegate.DeoptRanking())
            o.WriteLine($"  {hits} at pc {pc}");
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
    }

    /// <summary>And the answers are the interpreter's, under backtracking
    /// and with the callee mutating between runs -- a dynamic callee whose
    /// clauses changed must be seen as it is now, since the marker names
    /// the predicate and not an address that could have gone stale.
    /// </summary>
    [Fact]
    public void TheUncompiledCalleeIsSeenAsItIsNow()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("drive(_).").Success);
        Assert.True(tiered.Query("assertz(target(d, 4)).").Success);
        Assert.True(tiered.Query("drive(L), L == [a-1, b-2, c-3, d-4].").Success,
            "the meta-call did not see the clause added after it was resolved");
        Assert.True(tiered.Query("retract(target(a, 1)).").Success);
        Assert.True(tiered.Query("drive(L), L == [b-2, c-3, d-4].").Success,
            "the meta-call still saw a retracted clause");
    }
}
