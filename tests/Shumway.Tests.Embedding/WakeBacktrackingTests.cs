using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-049 stage 1: woken goals run as ordinary code in the flat
/// machine, so backtracking re-enters their alternatives. Issue #51's
/// examples are the anchor pins; the rest hold the edges — cut locality,
/// wake order, chained wakes, failure, exceptions, and the interrupted
/// goal's registers surviving a nondeterministic wake.</summary>
public sealed class WakeBacktrackingTests
{
    private static PrologEngine Co()
    {
        var e = new PrologEngine();
        e.UseCoroutining();
        return e;
    }

    [Fact]
    public void WokenGoal_BacktracksIntoItsAlternatives()
    {
        // The #51 report, verbatim.
        Assert.True(Co().Query("freeze(X, member(Y, [1,2])), X = a, Y = 2.").Success);
        var all = Co().Query(
            "findall(Y, (freeze(X, member(Y, [1,2,3])), X = a), L), L == [1,2,3].");
        Assert.True(all.Success);
    }

    [Fact]
    public void WokenDisjunction_BacktracksToo()
    {
        Assert.True(Co().Query("freeze(X, (Y = 1 ; Y = 2)), X = a, Y = 2.").Success);
    }

    [Fact]
    public void TheWakeRunsBetweenTheBindingAndTheNextGoal()
    {
        // Deterministic wakes behave exactly as before: bound before the
        // next goal runs, in freeze order, once each.
        var sol = Co().Query("freeze(X, Y = woke), X = 1, Z = Y.");
        Assert.True(sol.Success);
        Assert.Equal("woke", sol["Z"]!.ToString());
        Assert.True(Co().Query(
            "freeze(X, A = 1), freeze(X, B = 2), X = q, A == 1, B == 2.").Success);
    }

    [Fact]
    public void AFailedWakeFailsTheBinding()
    {
        Assert.False(Co().Query("freeze(X, fail), X = 1.").Success);
        // ...and the failure arrives where the binding was tried, so an
        // enclosing disjunction still has its alternative.
        Assert.True(Co().Query(
            "( freeze(X, fail), X = 1 ; true ).").Success);
    }

    [Fact]
    public void CutInAWokenGoal_IsLocalToThatGoal()
    {
        // call/1 semantics, pinned in the #47 arc and preserved here: the
        // cut commits the woken goal, never the caller that triggered it.
        Assert.False(Co().Query("freeze(X, ((!, fail) ; true)), X = a.").Success);
        Assert.True(Co().Query("( freeze(X, !), X = a, fail ; true ).").Success);
        // With alternatives now real, the cut has something to prune:
        // one solution, exactly as call((member(Y,[1,2]), !)).
        Assert.True(Co().Query(
            "findall(Y, (freeze(X, (member(Y, [1,2]), !)), X = a), L), L == [1].").Success);
    }

    [Fact]
    public void TheInterruptedGoalResumes_WhateverTheWakeDid()
    {
        // The binding goal's own arguments survive a wake that clobbers
        // every register: p(X, Y) must still see both its arguments after
        // the wake fired mid-head-unification.
        var e = Co();
        Assert.True(e.Query("assertz((p(V, W) :- V == bound, W == kept)).").Success);
        // The wake fires when p's first argument binds mid-head; a
        // nondeterministic wake ran a full goal over every register, and
        // p must still see BOTH its arguments on resume.
        Assert.True(e.Query(
            "freeze(F, member(_, [a,b,c])), F = bound, p(F, kept).").Success);
    }

    [Fact]
    public void ChainedWakes_FireInOrder()
    {
        var sol = Co().Query(
            "freeze(X, freeze(Y, Out = inner)), X = 1, Y = 2, Z = Out.");
        Assert.True(sol.Success);
        Assert.Equal("inner", sol["Z"]!.ToString());
    }

    [Fact]
    public void AWokenGoalCanThrow_AndCatchStillWorks()
    {
        Assert.True(Co().Query(
            "catch((freeze(X, throw(boom)), X = 1), boom, true).").Success);
        // A catch INSIDE the woken goal recovers and the binding proceeds.
        Assert.True(Co().Query(
            "freeze(X, catch(throw(t), t, true)), X = 1.").Success);
    }

    [Fact]
    public void NondeterministicWake_InterleavesWithLaterFailure()
    {
        // The classic shape the once-drain could never do: a later goal
        // fails, the search comes BACK through the woken goal's choice
        // points, and a different alternative satisfies it.
        Assert.True(Co().Query(
            "freeze(X, member(Y, [1,2,3])), X = go, member(Y, [2]), Y == 2.").Success);
        var count = Co().Query(
            "findall(Y-Z, (freeze(X, member(Y, [1,2])), X = a, member(Z, [p,q])), L), "
          + "L == [1-p, 1-q, 2-p, 2-q].");
        Assert.True(count.Success);
    }

    [Fact]
    public void DifStillCanonicalAndSound()
    {
        // dif/2 rides the same wake path; its deterministic re-check must
        // behave bit-identically.
        Assert.True(Co().Query("dif(X, Y), X = 1, Y = 2.").Success);
        Assert.False(Co().Query("dif(X, Y), X = 1, Y = 1.").Success);
        Assert.True(Co().Query(
            "dif(f(X, Y), f(Y, X)), X = 1, dif(X, Y), Y = 2.").Success);
    }

    [Fact]
    public void BacktrackingPastTheBinding_RestoresTheSuspension()
    {
        // Leaving the wake's own alternatives is one thing; undoing the
        // BINDING must restore the suspension, and a second binding fires
        // the goal again.
        Assert.True(Co().Query(
            "freeze(X, member(Y, [1,2])), ( X = a, Y = 9 ; X = b, Y = 2 ).").Success);
    }

    // ===== ADR-049 stage 2: the interrupt at Tier-1 region boundaries =====
    // The predicate that BINDS the frozen variable is promoted to IL, so the
    // wake fires from an emitted region boundary (the flush the drain used
    // to own), suspends the IL body via the tail-call bail, runs the driver
    // in the dispatch loop, and resumes by forward marker.

    private static PrologEngine Promoted(out int rounds)
    {
        var e = new PrologEngine();
        e.UseCoroutining();
        e.IlPromotion.Threshold = 3;
        e.ConsultString("""
            :- public bindit/2.
            :- public relay/2.
            bindit(V, V2) :- relay(V, V2).
            relay(V, V2) :- V = go, V2 = V.
            """);
        rounds = 8;
        return e;
    }

    [Fact]
    public void PromotedBinder_NondeterministicWakeBacktracks()
    {
        var e = Promoted(out int rounds);
        for (int i = 0; i < rounds; i++)
            Assert.True(e.Query(
                "freeze(X, member(Y, [1,2,3])), bindit(X, _), Y = 3.").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions());
        // Promoted now: the wake fires at the IL boundary and its
        // alternatives must still be re-enterable.
        for (int i = 0; i < 3; i++)
        {
            Assert.True(e.Query(
                "freeze(X, member(Y, [1,2,3])), bindit(X, _), Y = 3.").Success);
            Assert.True(e.Query(
                "findall(Y, (freeze(X, member(Y, [1,2])), bindit(X, _)), L), "
              + "L == [1,2].").Success);
        }
    }

    [Fact]
    public void PromotedBinder_CutInWokenGoalStaysLocal()
    {
        var e = Promoted(out int rounds);
        for (int i = 0; i < rounds; i++)
            Assert.True(e.Query("bindit(go, _).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions());
        Assert.False(e.Query(
            "freeze(X, ((!, fail) ; true)), bindit(X, _).").Success);
        Assert.True(e.Query(
            "( freeze(X, !), bindit(X, _), fail ; true ).").Success);
        // A cut in the CALLEE dispatched after the wake must not prune the
        // wake's alternatives (the resume re-establishes its barrier).
        Assert.True(e.Query(
            "findall(Y, (freeze(X, member(Y, [1,2])), bindit(X, _)), L), "
          + "L == [1,2].").Success);
    }

    [Fact]
    public void PromotedBinder_FailedWakeFailsTheBinding()
    {
        var e = Promoted(out int rounds);
        for (int i = 0; i < rounds; i++)
            Assert.True(e.Query("bindit(go, _).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions());
        Assert.False(e.Query("freeze(X, fail), bindit(X, _).").Success);
        Assert.True(e.Query("( freeze(X, fail), bindit(X, _) ; true ).").Success);
    }

    // ===== inline arithmetic is a goal boundary (ADR-049 close-out) =====
    // ADR-018 compiles `Z is Y+1` to a fused/inline op with no Call boundary,
    // so a wake queued by an earlier goal that binds Y never fired before the
    // arithmetic read Y — it read as unbound and threw instantiation_error.

    [Fact]
    public void InlineArithmeticFiresAPendingWakeFirst()
    {
        var e = Co();
        var s = e.Query("freeze(X, Y = 5), X = 1, Z is Y + 1.");
        Assert.True(s.Success);
        Assert.Equal("6", s["Z"]!.ToString());
    }

    [Fact]
    public void InlineComparisonFiresAPendingWakeFirst()
    {
        Assert.True(Co().Query("freeze(X, Y = 7), X = 1, Y > 5.").Success);
        Assert.False(Co().Query("freeze(X, Y = 7), X = 1, Y < 5.").Success);
    }

    [Fact]
    public void AMultiStepExpressionFiresTheWakeAtItsStart()
    {
        // The AEvalPush sequence (not the fused op): the flush is at the
        // first push — draining mid-expression would clobber the shared
        // evaluation stack.
        var s = Co().Query("freeze(X, Y = 2), X = 1, Z is Y*Y + 1.");
        Assert.True(s.Success);
        Assert.Equal("5", s["Z"]!.ToString());
        var t = Co().Query("freeze(X, Y = 3), X = 1, Z is (Y + Y) * 2.");
        Assert.True(t.Success);
        Assert.Equal("12", t["Z"]!.ToString());
    }

    [Fact]
    public void AWakeThatEvaluatesArithmeticDoesNotClobberTheEvalStack()
    {
        // The drain during operand read runs a wake goal that ITSELF does
        // arithmetic on the same static eval stack. A later operand of the
        // outer expression, already pushed, must survive: nested evaluation
        // is balanced (pushes above, pops back). Second operand unbound so
        // the flush lands mid-expression.
        var s = Co().Query(
            "freeze(X, Y is 3 * 4), X = 1, Z is 100 + Y.");
        Assert.True(s.Success);
        Assert.Equal("112", s["Z"]!.ToString());
    }

    [Fact]
    public void AFailedWakeFailsTheArithmeticGoal()
    {
        // The drain reports failure, and the inline op backtracks rather
        // than throwing — a failed wake fails the binding that triggered it.
        Assert.False(Co().Query("freeze(X, fail), X = 1, _ is X + 1.").Success);
        Assert.True(Co().Query(
            "( freeze(X, fail), X = 1, _ is X + 1 ; true ).").Success);
    }

    [Fact]
    public void PromotedInlineArithmeticFiresAPendingWakeToo()
    {
        // The ADR-018 arithmetic ops are emitted in Tier-1 IL as well; the
        // promoted body must flush a pending wake before its inline arithmetic
        // reads the operand, exactly as Tier-0 does.
        var e = new PrologEngine();
        e.UseCoroutining();
        e.IlPromotion.Threshold = 3;
        e.ConsultString(":- public aw/3.\naw(X, Y, Z) :- X = 1, Z is Y + 1.\n");
        for (int i = 0; i < 8; i++)
            Assert.True(e.Query("aw(1, 4, _).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions());
        for (int i = 0; i < 3; i++)
        {
            var s = e.Query("freeze(X, Y = 5), aw(X, Y, Z).");
            Assert.True(s.Success);
            Assert.Equal("6", s["Z"]!.ToString());
        }
    }

    [Fact]
    public void WhenAndFrozenReporting_Unmoved()
    {
        Assert.True(Co().Query(
            "when(nonvar(X), (integer(X) ; atom(X))), X = foo.").Success);
        Assert.True(Co().Query(
            "freeze(X, member(X, [a])), frozen(X, G), G \\== true, X = a.").Success);
    }
}
