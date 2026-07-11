using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 147: <see cref="Shumway.Core.Activation.Cut"/>'s trail
/// compaction can drop entries above any surviving catch frame's
/// snapshot. Pre-fix the catch frame kept its stale snap value, so
/// a later throw's <c>UnwindToCatchFrame</c> asked
/// <c>UnwindTrails</c> to roll back to a non-existent trail
/// position and crashed with ArgumentOutOfRangeException.
///
/// <para>The user's 2016 GProlog year-arithmetic script surfaced
/// the bug: <c>expand_exp</c> contains a cut inside a
/// <c>catch/3</c>; an arithmetic-error throw later unwound to the
/// catch and hit the stale snap.</para>
/// </summary>
public class Chunk147Tests
{
    [Fact]
    public void CutInsideCatch_ThenThrow_NoCrash()
    {
        // Minimal reproducer following the user-program shape:
        // a goal inside catch/3 that:
        //   1. Pushes a CP via a multi-solution call (between/3).
        //   2. Cuts (commits to the current solution).
        //   3. Triggers an arithmetic error (zero divisor).
        //
        // The cut's trail compaction trimmed the binding trail past
        // the catch-frame snapshot. Then the throw unwound back to
        // the catch — the broken path.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch( "
            + "  ( between(1, 5, X), !, _Y is X // 0 ), "
            + "  _Error, "
            + "  Caught = ok ).").Success);
    }

    [Fact]
    public void NestedCatchesWithCutsAndThrows_AllRecover()
    {
        // Deeper case: two nested catches, each with cuts inside,
        // each catching their own thrown error. Stresses the
        // compaction-clip across multiple catch-frame snapshots.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch( "
            + "  catch( "
            + "    ( member(_X, [1,2,3]), !, throw(inner) ), "
            + "    inner, "
            + "    Inner = caught), "
            + "  _, "
            + "  fail ).").Success);
    }
}
