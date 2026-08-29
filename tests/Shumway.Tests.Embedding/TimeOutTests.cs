using System;
using System.Diagnostics;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>time_out/3</c> — SICStus semantics: milliseconds,
/// <c>success</c> / <c>time_out</c>, NON-deterministic, and the clock
/// RESTARTS when the goal is re-entered on backtracking, so the limit bounds
/// each solution rather than the whole enumeration.
///
/// <para>The limit is enforced at the engine's safe points (the same ones
/// cancellation uses), which is what lets a failure-driven loop be
/// interrupted even though it allocates nothing.</para></summary>
// Exclusive for a different reason than the others: nothing here mutates
// process state, but these tests pin WALL-CLOCK deadlines whose margins are
// tight by design (per-solution restart needs each solution under the limit
// and the whole enumeration over it). Under 4 processes × 3 threads on a
// 4-core runner, scheduling stretched a 250 ms solution past its 400 ms
// budget. The exclusive phase runs alone in the process, after the buckets.
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class TimeOutTests
{
    private static string ResultOf(PrologEngine e, string goal, int ms)
    {
        var sol = e.Query($"time_out(({goal}), {ms}, R).");
        Assert.True(sol.Success);
        return ((AtomTerm)sol["R"]!).Name;
    }

    [Fact]
    public void AGoalThatFinishes_ReportsSuccess()
    {
        Assert.Equal("success", ResultOf(new PrologEngine(), "true", 1000));
    }

    [Fact]
    public void AFailureDrivenLoop_IsInterrupted()
    {
        // (repeat, fail) allocates no heap and crosses no call boundary — only
        // the backtrack-path safe point can see the deadline.
        var sw = Stopwatch.StartNew();
        Assert.Equal("time_out", ResultOf(new PrologEngine(), "repeat, fail", 300));
        sw.Stop();
        Assert.InRange(sw.ElapsedMilliseconds, 100, 15000);
    }

    [Fact]
    public void ARecursiveLoop_IsInterrupted()
    {
        var e = new PrologEngine();
        e.ConsultString("spin :- spin.");
        Assert.Equal("time_out", ResultOf(e, "spin", 300));
    }

    [Fact]
    public void AFailingGoal_Fails_RatherThanReportingAnything()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("time_out(fail, 1000, _).").Success);
    }

    [Fact]
    public void AnException_PropagatesUnchanged()
    {
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(
            () => e.Query("time_out(throw(my_ball), 1000, _)."));
        Assert.Contains("my_ball", ex.Message);
    }

    [Fact]
    public void SolutionsArePreserved()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "findall(X, time_out(member(X, [a,b,c]), 5000, _), L).");
        Assert.True(sol.Success);
        Assert.Equal("[a, b, c]", AstTermRenderer.Render(sol["L"]!));
    }

    [Fact]
    public void TheClockRestartsOnBacktracking()
    {
        // Three solutions, each costing more than HALF the limit: under a
        // whole-enumeration limit this could not complete, and under a
        // per-solution one it comfortably does. That difference IS the
        // SICStus semantics being pinned.
        var e = new PrologEngine();
        e.ConsultString("slow(X) :- member(X, [1,2,3]), sleep(0.25).");
        var sol = e.Query("findall(X, time_out(slow(X), 400, _), L).");
        Assert.True(sol.Success);
        Assert.Equal("[1, 2, 3]", AstTermRenderer.Render(sol["L"]!));
    }

    [Fact]
    public void NestedCalls_TakeTheTighterDeadline()
    {
        // An inner call may narrow the budget but must not outlive the outer
        // promise: the outer 300ms governs even though the inner asks for more.
        var e = new PrologEngine();
        e.ConsultString("spin :- spin.");
        Assert.Equal("time_out",
            ResultOf(e, "time_out(spin, 60000, _)", 300));
    }

    [Fact]
    public void AnUnboundLimit_IsAnInstantiationError()
    {
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(() => e.Query("time_out(true, _, _)."));
        Assert.Contains("instantiation_error", ex.Message);
    }

    [Fact]
    public void TheEngineIsUsableAfterATimeout()
    {
        // The deadline stack must unwind — otherwise the next goal inherits a
        // deadline that already expired and everything times out forever.
        var e = new PrologEngine();
        Assert.Equal("time_out", ResultOf(e, "repeat, fail", 200));
        Assert.True(e.Query("atom_length(abc, 3).").Success);
        Assert.Equal("success", ResultOf(e, "true", 1000));
    }
}
