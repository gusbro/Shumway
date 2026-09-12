using System.IO;
using Shumway.Builtins;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The heap GC's precise live-register bound assumes the WAM
/// caller-saved discipline: at a call boundary the live X registers are
/// exactly the callee's arguments. The interpreter keeps that promise;
/// REGION-compiled IL does not -- a body-once region holds values in X across
/// a call of smaller arity, such as a bare <c>true</c> in an if-then-else
/// with the next goal's structure already built beside it. A collection
/// bounded there moved a structure a live register still pointed at, and the
/// register then read relocated garbage: call_nth/2 through the top level
/// died with a nonsense <c>must_be</c> error at whatever iteration crossed
/// the GC watermark (~48,000 of its allocation rate), because call_nth's
/// gensym is exactly that shape once promoted.
///
/// <para>Under IL, bounded safe points now scan the full register bank; pure
/// Tier-0 keeps the precise bound and the stale-register retention fix it
/// carries. The honest cost: a dead high register can pin its structure
/// under Tier-1 until the emitter can say what is really live.</para></summary>
public sealed class GcRegisterBoundTier1Tests
{
    private static PrologEngine Promoted()
    {
        BuiltinsRegistry.Register("$gcstress", 0,
            a => { a.GcStressMode = true; return true; });
        var e = new PrologEngine { Out = new StringWriter() };
        e.IlPromotion.Threshold = 32;
        e.IlPromotion.PgoSampleThreshold = int.MaxValue;
        e.ConsultString("""
            step(Goal) :- gensym('$k', _), call(Goal).
            loop(0) :- !.
            loop(N) :- step(true), M is N - 1, loop(M).
            nth(0) :- !.
            nth(N) :- call_nth(true, 1), M is N - 1, nth(M).
            """);
        // Cross the promotion threshold across queries, so the stressed run
        // below executes as compiled IL -- the shape the bound broke under.
        for (int i = 0; i < 50; i++)
            Assert.True(e.Query("loop(40), nth(2).").Success);
        e.IlPromotion.WaitForPendingPromotions(120_000);
        return e;
    }

    /// <summary>A collection at EVERY safe point, under promoted IL: every
    /// structure a live register points at must survive every one of them.
    /// Before the fix this died in the first hundred iterations.</summary>
    [Fact]
    public void PromotedMetaCallSurvivesACollectionAtEverySafePoint()
    {
        var e = Promoted();
        Assert.True(e.Query("'$gcstress', loop(300).").Success);
        Assert.True(e.Query("'$gcstress', nth(300).").Success);
    }

    /// <summary>The user-visible symptom, at its original size: call_nth/2
    /// across the real GC watermark, no stress mode -- the collection fires
    /// where the allocation rate puts it.</summary>
    [Fact]
    public void CallNthCrossesTheGcWatermarkIntact()
    {
        var e = Promoted();
        Assert.True(e.Query("nth(60000).").Success);
        // And the engine is not quietly poisoned afterwards.
        Assert.True(e.Query("must_be(atom, ok), gensym('$k', A), atom(A).").Success);
    }
}
