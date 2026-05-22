using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 112 (Phase 8): re-verifying <c>between/3</c> in a failure-driven
/// loop. A Phase-8 backlog item recorded that the textbook constant-stack
/// loop idiom — <c>between(1, BigN, _), ( Step -&gt; fail ; ! )</c> —
/// "hung / crashed" when first tried (for the tabling fixpoint). These
/// tests check it directly; it works — the original crash was the
/// chunk-111 list-materialisation overflow, not <c>between/3</c>.
/// </summary>
public class Chunk112Tests
{
    [Fact]
    public void FailureDrivenLoop_OverBetween_Terminates()
    {
        // Half a million iterations of the classic ( Gen, fail ; true )
        // loop — constant stack, must simply finish.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "( between(1, 500000, _), fail ; true ).").Success);
    }

    [Fact]
    public void BetweenWithIfThenElseFailCut_Terminates()
    {
        // The exact shape from the backlog: between drives the loop, an
        // if-then-else either fails (continue) or cuts (stop).
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "between(1, 500000, N), ( N < 500000 -> fail ; ! ).").Success);
    }

    [Fact]
    public void FailureDrivenLoop_AccumulatesSideEffects()
    {
        // assertz side effects from inside the loop persist across the
        // backtracking that drives it.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic seen/1.");
        engine.Query(
            "( between(1, 1000, K), assertz(seen(K)), fail ; true ).");
        Assert.Equal(1000, engine.QueryAll("seen(X).").Count());
    }
}
