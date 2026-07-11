using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 119 (Phase 8, ADR-015 chunk E): amortised program growth.
///
/// <para>Chunk C appends a freshly recompiled dynamic predicate to the
/// program buffer on each mid-query modification. <c>Activation.AppendCode</c>
/// used to re-copy the whole (growing) buffer every append — O(n³) for a
/// query that asserts-then-calls a dynamic predicate in a loop. Capacity
/// doubling makes the append amortised O(1); the worst case is now O(n²),
/// dominated by whole-predicate recompilation rather than buffer copying.</para>
/// </summary>
public class Chunk119Tests
{
    [Fact]
    public void LongAssertThenCallLoop_Completes()
    {
        // The pathological pattern: each iteration asserts a clause and
        // immediately calls the (now stale) predicate, forcing a recompile.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic d/1.");
        Assert.True(engine.Query(
            "( between(1, 500, K), assertz(d(K)), d(K), fail ; true ).").Success);
        // Every clause landed and stays callable.
        Assert.Equal(500, engine.QueryAll("d(_).").Count());
        Assert.True(engine.Query("d(1), d(250), d(500).").Success);
    }

    [Fact]
    public void InterleavedAssertRetractCall_StaysConsistent()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic d/1.");
        // Assert 1..100, then retract the even ones, calling between edits.
        Assert.True(engine.Query(
            "( between(1, 100, K), assertz(d(K)), d(K), fail ; true ).").Success);
        Assert.True(engine.Query(
            "( between(1, 100, K), 0 is K mod 2, retract(d(K)), fail ; true ).").Success);
        Assert.Equal(50, engine.QueryAll("d(_).").Count());
        Assert.True(engine.Query("d(1), \\+ d(2), d(99), \\+ d(100).").Success);
    }
}
