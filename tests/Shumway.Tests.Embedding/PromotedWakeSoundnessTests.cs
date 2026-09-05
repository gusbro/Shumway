using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Coroutine wakeups must survive Tier-1 promotion. The lost-wake
/// matrix this pins: a predicate whose PROMOTED body re-binds an attributed
/// variable (length/2's enumeration decomposing a frozen list) ran its
/// self-recursion as an in-method loop with no goal boundary, so the wake a
/// head-match queued never fired — freeze/2 went silently unhooked and
/// unsound answers were delivered. Fixed at four layers: the queue carries
/// each wake's attvar home so dead wakes are dropped by MARK instead of
/// blanket-cleared on backtrack (TryBacktrack / FailIlGuard kept eating
/// wakes of surviving bindings), the IL self-tail back-edge fires the same
/// wake boundary the dispatch loop it bypasses would have fired, and the
/// threaded-transfer and answer boundaries check as well.</summary>
public sealed class PromotedWakeSoundnessTests
{
    private static PrologEngine Warmed()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        // Embedding defaults to Tier-0 (Threshold 0); the lost-wake shapes
        // need real promotion, the REPL's default.
        e.IlPromotion.Threshold = 32;
        Assert.True(e.Query("use_module(library(coroutining)).").Success);
        // Drives $length_enum past the promotion threshold via its
        // retry-counted path, exactly the shape that lost wakes.
        Assert.True(e.Query("once(( length(_, N), N > 100000 )).").Success);
        return e;
    }

    [Fact]
    public void FailingFrozenGoal_PrunesThePromotedEnumeration()
    {
        var e = Warmed();
        Assert.False(e.Query("freeze(X, fail), length(X, N), N > 1.").Success);
    }

    [Fact]
    public void FrozenUnificationGoal_StopsTheEnumeration()
    {
        // freeze(X, X=[]) fires on the first candidate binding; [] never
        // unifies with [_|_], so no N > 3 answer exists. The unsound engine
        // answered X = [_,_,_,_], N = 4.
        var e = Warmed();
        Assert.False(e.Query("freeze(X, X=[]), length(X, N), N > 3.").Success);
    }

    [Fact]
    public void AbortedGrindLeavesTheEngineSound()
    {
        // The original discovery recipe: a timed-out length grind (promoting
        // mid-enumeration, unwound by the timeout ball), then the freeze test
        // in a LATER query on the same engine. The grind is the open
        // enumeration driven by failure, since length(L, L) is refused at once
        // now and grinds nothing.
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        e.IlPromotion.Threshold = 32;
        Assert.True(e.Query("use_module(library(coroutining)).").Success);
        Assert.True(e.Query(
            "time_out((length(_L, _N), fail), 300, R), R == time_out.").Success);
        Assert.False(e.Query("freeze(X, X=[]), length(X, N), N > 3.").Success);
    }

    [Fact]
    public void SucceedingFrozenGoal_FiresOncePerBindingAndProceeds()
    {
        var e = Warmed();
        Assert.True(e.Query(
            "freeze(X, assertz(zz_fired)), length(X, N), N > 3, !.").Success);
        Assert.True(e.Query("zz_fired.").Success);
    }

    [Fact]
    public void WakesOfSurvivingBindingsOutliveYoungerBacktracks()
    {
        // Tier-0 shape of the blanket-clear defect: bind (queueing a wake),
        // then fail something younger — the wake must still fire and fail
        // the whole conjunction.
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.True(e.Query("use_module(library(coroutining)).").Success);
        Assert.False(e.Query(
            "freeze(X, fail), ( X = 1 ; X = 2 ).").Success);
    }

    [Fact]
    public void DifAndPlainFreezeStayHealthyOnTheWarmedEngine()
    {
        var e = Warmed();
        Assert.True(e.Query("freeze(A, A = 1), A = 1.").Success);
        Assert.False(e.Query("dif(P, Q), P = Q.").Success);
        Assert.True(e.Query("dif(P, Q), P = 1, Q = 2.").Success);
    }
}
