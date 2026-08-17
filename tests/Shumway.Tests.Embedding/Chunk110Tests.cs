using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 110 (Phase 8): last-call optimisation regression tests.
///
/// <para>The Phase-8 backlog opened with "no LCO", inferred from the
/// tabling fixpoint overflowing on a deep recursion. Investigation showed
/// that inference was wrong: the WAM compiler emits <c>deallocate</c>
/// before the final <c>execute</c>, and a plain tail-recursive predicate
/// runs a hundred thousand calls deep in constant control stack — these
/// tests pin that down. The backlog item was corrected to the real,
/// narrower problem: deep recursion that threads a deep data structure
/// (and the deep tabling fixpoint) still overflows, for a reason that is
/// not absent LCO and is not yet isolated.</para>
/// </summary>
public class Chunk110Tests
{
    [Fact]
    public void PlainTailRecursion_RunsDeepInConstantStack()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            count(0).
            count(N) :- N > 0, N1 is N - 1, count(N1).
            """);
        Assert.True(engine.Query("count(100000).").Success);
    }

    [Fact]
    public void TailRecursionThroughIfThenElse_RunsDeep()
    {
        // The recursive call sits in the then-branch of (->)/2 — still a
        // last call, still optimised.
        var engine = new PrologEngine();
        engine.ConsultString("loop(N) :- ( N > 0 -> N1 is N - 1, loop(N1) ; true ).");
        Assert.True(engine.Query("loop(100000).").Success);
    }
}
