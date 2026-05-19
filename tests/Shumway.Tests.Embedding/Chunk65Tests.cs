using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 65: full coverage characterization for the head-var
/// preservation pass. Chunk 62 already tightened the
/// <c>NeedsSaveForGoal</c> rule to skip preserves when the goal's
/// home-clobbering put fires after V's reads in arg-index order.
/// The remaining optimisation surface — Warren's classical
/// dependency-graph scheduler with cycle-breaking via temporaries —
/// is a constant-factor saving rather than a correctness gap; an
/// attempt during chunk 65 to push the existing rule further
/// (allow compounds at position ≤ home without saving) destabilised
/// the test suite because compound-emission temporaries
/// occasionally land back inside the same arg range. The chunk's
/// deliverable is end-to-end pinning of the patterns the current
/// pass <em>must</em> get right.
/// </summary>
public class Chunk65Tests
{
    [Fact]
    public void Shuffle_SimpleSwap_StillCorrect()
    {
        // foo(X, Y) :- bar(Y, X). — the classic swap, would need a
        // Warren temp to avoid two saves. Current pass saves both
        // upfront, but the result is still correct.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/2.\n" +
            ":- public bar/2.\n" +
            "foo(X, Y) :- bar(Y, X).\n" +
            "bar(b, a).\n");
        Assert.True(engine.Query("foo(a, b).").Success);
        Assert.False(engine.Query("foo(b, a).").Success);
    }

    [Fact]
    public void Shuffle_ThreeArgRotate_StillCorrect()
    {
        // foo(X, Y, Z) :- bar(Z, X, Y). — a 3-cycle that Warren's
        // algorithm would handle with one temp + three moves; current
        // pass uses more moves but still produces the right bindings.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/3.\n" +
            ":- public bar/3.\n" +
            "foo(X, Y, Z) :- bar(Z, X, Y).\n" +
            "bar(c, a, b).\n");
        Assert.True(engine.Query("foo(a, b, c).").Success);
        Assert.False(engine.Query("foo(c, b, a).").Success);
    }

    [Fact]
    public void Shuffle_ReadBeforeWrite_NeedsNoSave()
    {
        // foo(X) :- bar(X, X). — X read at arg 0 (before home if home
        // == 0) AND at arg 1. The flat-read at position 0 fires
        // before the put-to-position-0 clobber, so reads at later
        // positions still need to be checked.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/1.\n" +
            ":- public bar/2.\n" +
            "foo(X) :- bar(X, X).\n" +
            "bar(7, 7).\n");
        Assert.True(engine.Query("foo(7).").Success);
        Assert.False(engine.Query("foo(8).").Success);
    }

    [Fact]
    public void Shuffle_CompoundArgAtLowPosition_StillCorrect()
    {
        // foo(X) :- bar([X], X). — X appears inside a compound at
        // position 0, then flat at position 1. The conservative
        // pass saves X upfront because compound emission may use
        // anonymous temps that could overlap with low-position
        // arg slots in pathological cases — see chunk 65's
        // attempted lift of this rule.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/1.\n" +
            ":- public bar/2.\n" +
            "foo(X) :- bar([X], X).\n" +
            "bar([7], 7).\n");
        Assert.True(engine.Query("foo(7).").Success);
        Assert.False(engine.Query("foo(8).").Success);
    }
}
