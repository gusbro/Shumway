using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 113 (Phase 8): the <c>repeat/0</c> builtin, and verification of
/// the dynamic-database builtins.
///
/// <para><c>clause/2</c>, <c>retract/1</c> and <c>abolish/1</c> consult
/// the live dynamic store, so a change made earlier in the same query is
/// visible to them. A <em>direct call</em> to a dynamic predicate still
/// sees only the query-setup snapshot — that is a recorded Phase-8 item
/// (it needs runtime-resolved dynamic dispatch, an engine change).</para>
/// </summary>
public class Chunk113Tests
{
    private static PrologEngine Dyn()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        return e;
    }

    [Fact]
    public void Repeat_WithCut_SucceedsOnce()
        => Assert.True(new PrologEngine().Query("repeat, !.").Success);

    [Fact]
    public void Repeat_DrivesAGeneratorToACondition()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic ctr/1.
            ctr(0).
            """);
        // A repeat-driven failure loop: bump the counter until it reaches 5.
        Assert.True(e.Query(
            "repeat, retract(ctr(N)), N1 is N + 1, assertz(ctr(N1)), N1 >= 5, !.")
            .Success);
        Assert.True(e.Query("ctr(5).").Success);
    }

    [Fact]
    public void AssertThenClause_SameQuery()
        => Assert.True(Dyn().Query("assertz(d(1)), clause(d(1), true).").Success);

    [Fact]
    public void AssertThenRetract_SameQuery()
        => Assert.True(Dyn().Query("assertz(d(1)), retract(d(1)).").Success);

    [Fact]
    public void AssertThenAbolish_SameQuery()
        => Assert.True(Dyn().Query(
            "assertz(d(1)), abolish(d/1), \\+ clause(d(_), _).").Success);
}
