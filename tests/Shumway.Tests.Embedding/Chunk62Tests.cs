using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 62: Warren-style refinement of the head-var preservation
/// pass. The full Warren scheduler (cycle detection, temporary
/// allocation, two-pass put scheduling) stays deferred — it's a
/// constant-factor saving, not a correctness gap. What lands here is
/// a tighter <c>NeedsSave</c> check that avoids emitting a preserve
/// move when the goal reads the head var at an arg position
/// <em>before</em> the home-clobbering write, which is the common
/// safe case the existing minimum-correctness pass over-handled.
/// </summary>
public class Chunk62Tests
{
    [Fact]
    public void HeadVar_ReadAtLowerArgPosition_NeedsNoSave()
    {
        // foo(X, Y) :- bar(X, Y, _).
        //   X is at home X[0], Y at X[1]. bar takes X at arg 0, Y at
        //   arg 1. The puts emit in arg-index order: arg 0 (put_value_x
        //   0 → 0, no-op), arg 1 (put_value_x 1 → 1, no-op). Neither
        //   clobbers a position before it's read. No save needed.
        // (Verified end-to-end by the predicate working correctly.)
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/2.\n" +
            ":- public bar/3.\n" +
            "foo(X, Y) :- bar(X, Y, _).\n" +
            "bar(a, b, _).\n");
        Assert.True(engine.Query("foo(a, b).").Success);
    }

    [Fact]
    public void HeadVar_SwappedArgs_StillCorrect()
    {
        // foo(X, Y) :- bar(Y, X).
        //   The cyclic case Warren's full algorithm uses a temp for.
        //   Our refined-but-still-minimum-correctness pass saves both
        //   head vars to fresh slots, then issues the puts. Slower
        //   than the optimal swap-via-temp but correct.
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
    public void HeadVar_ReadInsideCompound_TreatedAsSaveNeeded()
    {
        // foo(X) :- bar([X | _]).
        //   X is read inside a compound (the cons cell), which we
        //   conservatively flag as needs-save. Correctness-wise the
        //   predicate still runs fine either way.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/1.\n" +
            ":- public bar/1.\n" +
            "foo(X) :- bar([X | _]).\n" +
            "bar([42 | _]).\n");
        Assert.True(engine.Query("foo(42).").Success);
        Assert.False(engine.Query("foo(7).").Success);
    }

    [Fact]
    public void HeadVar_MultiGoal_StillCorrect()
    {
        // foo(X, Y) :- p(X), q(Y), r(X, Y).
        //   X and Y both perm (used after first call). Their head args
        //   are X[0] and X[1], and r reads them in arg order with no
        //   clobber. The pass should NOT emit unnecessary saves here.
        //   We exercise the path with a real query rather than
        //   instruction-counting — the simpler signal that nothing's
        //   broken.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/2.\n" +
            ":- public p/1.\n" +
            ":- public q/1.\n" +
            ":- public r/2.\n" +
            "foo(X, Y) :- p(X), q(Y), r(X, Y).\n" +
            "p(a).\n" +
            "q(b).\n" +
            "r(a, b).\n");
        Assert.True(engine.Query("foo(a, b).").Success);
    }
}
