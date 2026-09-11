using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using Shumway.Builtins;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A catch frame is never popped, only marked inactive, because its
/// push and deactivate records are still on the extra trail and a physically
/// shorter stack underflows the replay. Leaving control of a guarded goal
/// therefore had to FIND the top-most still-active frame, and it did that by
/// scanning down from the top -- past every frame the ascent had already
/// closed.
///
/// <para>So a nest of catch/3 cost n(n+1)/2 steps to unwind, exactly: 100,000
/// deep took twelve seconds where an engine that does it in one step takes
/// a tenth of that. The scan now starts from a remembered upper bound, which
/// is the only thing it can be -- too high costs a few steps and never a
/// wrong answer.</para></summary>
public sealed class NestedCatchScalingTests
{
    private const string Program = """
        deep(0, G) :- !, G.
        deep(N, G) :- M is N - 1, catch(deep(M, G), -, true).
        """;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(Program);
        return e;
    }

    /// <summary>The shape of the cost, not its size. Quadratic at this depth
    /// is around fifty seconds and linear is under a second, so the bound is
    /// generous enough that a slow or loaded machine cannot flake it while
    /// still being twenty times under the regression.</summary>
    [Fact]
    public void UnwindingADeepCatchNestIsNotQuadratic()
    {
        var e = Engine();
        var sw = Stopwatch.StartNew();
        Assert.True(e.Query("deep(200000, true).").Success);
        sw.Stop();
        Assert.True(sw.Elapsed.TotalSeconds < 15,
            $"200,000 nested catch/3 took {sw.Elapsed.TotalSeconds:F1}s; "
            + "it was quadratic in the nest depth before and is linear now");
    }

    /// <summary>ANTI-VACUITY, and the reason the frames cannot simply be
    /// popped: the nest still CATCHES, at the right depth, and the recovery
    /// runs in the right place.</summary>
    [Fact]
    public void ADeepNestStillCatchesAtTheRightFrame()
    {
        var e = Engine();
        // Thrown from the bottom of a 10,000-deep nest: the innermost frame
        // whose catcher matches takes it, and `-` matches nothing thrown here,
        // so it travels to the outer catch/3.
        Assert.True(e.Query(
            "catch(deep(10000, throw(ball)), ball, true).").Success);
        // A ball the nest DOES catch is caught by the innermost frame, so the
        // goal simply succeeds without reaching the outer catcher.
        Assert.True(e.Query("deep(10000, throw(-)).").Success);
        // And a ball no frame in the nest matches escapes ALL of them,
        // including the outer catcher, which is `-` too.
        Assert.Throws<ShumwayPrologException>(
            () => e.Query("catch(deep(100, throw(other)), -, true)."));
    }

    /// <summary>Backtracking INTO a guarded goal re-activates its frame, which
    /// is what the scan bound has to respect: after redoing, the catch is live
    /// again and takes the ball.</summary>
    [Fact]
    public void BacktrackingBackIntoAGuardedGoalRestoresItsCatcher()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            side(1).
            side(2).
            guarded(X) :- side(X), (X =:= 2 -> throw(boom) ; true).
            go(X) :- catch(guarded(X), boom, X = caught).
            """);
        // First solution comes back normally; forcing the second re-enters the
        // guarded goal, which throws, and the frame that was deactivated on
        // the way out has to be catching again.
        var all = e.QueryAll("go(X).").ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal("caught", ((Shumway.Compiler.Ast.AtomTerm)all[1]["X"]!).Name);
    }

    /// <summary>Sequential (non-nested) catches leave inactive frames behind
    /// them, and the next one still has to find its own: the bound is per
    /// frame, not per query.</summary>
    [Fact]
    public void CatchesInSequenceEachFindTheirOwnFrame()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            seq(0) :- !.
            seq(N) :- catch(true, -, true), M is N - 1, seq(M).
            """);
        Assert.True(e.Query("seq(20000).").Success);
        Assert.True(e.Query(
            "catch((catch(true, a, true), catch(throw(b), b, true)), _, fail).")
            .Success);
    }

    /// <summary>A frame is otherwise reclaimed only by BACKTRACKING, through
    /// its push record, so a deterministic loop kept one per catch/3 forever:
    /// four million calls were most of a gigabyte. An exit that nothing can
    /// come back to now drops the frame outright.
    ///
    /// <para>Counted rather than weighed. Managed memory was the obvious
    /// measure and the wrong one: the loop also grows the PROLOG heap by two
    /// cells a call, which at these sizes is the same order as the frames, so
    /// the test was really measuring legitimate allocation and it failed on
    /// .NET Framework where the two land differently. The frame count is the
    /// thing the fix is about, it is exact, and it is the same number on every
    /// runtime.</para></summary>
    [Fact]
    public void ADeterministicLoopDoesNotKeepAFramePerCatch()
    {
        BuiltinsRegistry.Register("$catch_frames", 1,
            a => a.UnifyRegisterWithCell(0, Cell.Int(a.CatchFrameCount)));
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            loop(0) :- !.
            loop(N) :- catch(true, -, true), M is N - 1, loop(M).
            """);
        // 200,000 calls left 200,000 frames standing. The bound is loose --
        // what matters is that it does not grow with the loop.
        Assert.True(
            e.Query("loop(200000), '$catch_frames'(C), C < 100.").Success,
            "a frame per catch/3 is being kept");
    }

    /// <summary>ANTI-VACUITY for the reclamation, and the condition it turns
    /// on: a choice point that outlives the guarded goal is exactly what can
    /// come back for the catcher, so the frame must NOT be dropped then. The
    /// third solution throws, and the catch has to still be there.</summary>
    [Fact]
    public void ALiveChoicePointKeepsTheCatcher()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            side(1).
            side(2).
            side(3).
            guarded(X) :- side(X), (X =:= 3 -> throw(boom) ; true).
            go(X) :- catch(guarded(X), boom, X = caught).
            """);
        var all = e.QueryAll("go(X).").ToList();
        Assert.Equal(3, all.Count);
        Assert.Equal("caught", ((Shumway.Compiler.Ast.AtomTerm)all[2]["X"]!).Name);
    }
}
