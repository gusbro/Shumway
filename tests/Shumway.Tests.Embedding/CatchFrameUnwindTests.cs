using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The nested-driver failure path and the catch-frame stack: an attribute
/// hook (run via RunGoalInEngine) that opens-and-closes a catch/3 and THEN
/// fails must leave the frame stack consistent with the extra trail. The
/// old path physically REMOVED the goal's frames while their push/deactivate
/// records stayed on the trail, so a later outer backtrack replayed those
/// records against a shorter stack — IndexOutOfRange deep in TrustMe
/// (Trealla's clpb consistency test0360, sat(C#D) vetoing C=D inside \+,
/// was the finder). Frames are now DEACTIVATED trailed and die when their
/// own push entries unwind.
/// </summary>
public class CatchFrameUnwindTests
{
    private static PrologEngine Co()
    {
        var e = new PrologEngine();
        e.Query("use_module(library(coroutining)).");
        return e;
    }

    [Fact]
    public void HookWithInnerCatch_ThenFailure_SurvivesOuterBacktrack()
    {
        // The hook goal completes a catch/3 (push + deactivate trailed),
        // then FAILS (vetoing the unification). The \+ succeeds; the
        // trailing `fail ; true` then unwinds the whole region — which
        // replays the catch-frame trail records.
        Assert.True(Co().Query(
            "( freeze(X, (catch(true, _, true), fail)), \\+ X = 1, fail"
            + "; true ).").Success);
    }

    [Fact]
    public void HookWithNestedCatches_ThenFailure_SurvivesOuterBacktrack()
    {
        Assert.True(Co().Query(
            "( freeze(X, (catch(catch(true, _, true), _, true), "
            + "catch(true, _, true), fail)), \\+ X = a, fail"
            + "; true ).").Success);
    }

    [Fact]
    public void HookFailure_DoesNotDisableALaterCatch()
    {
        // The deactivated leftover frames must not swallow or mis-route a
        // later ball: a throw after the failed hook still reaches ITS catch.
        Assert.True(Co().Query(
            "freeze(X, (catch(true, _, true), fail)), \\+ X = 1, "
            + "catch(throw(ball), B, true), B == ball.").Success);
    }
}
