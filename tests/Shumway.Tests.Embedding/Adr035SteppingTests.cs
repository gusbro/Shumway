using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 phase D1 — <see cref="DebugService"/>: breakpoints, port-based stepping,
/// and the call stack recomposed from the environment chain.
///
/// <para>A Prolog goal has four ways in and out — call, exit, redo, fail — and the
/// last two cannot be expressed by stepping over return addresses, which is all a
/// conventional frame-based debugger knows how to do. So a step here is "run until
/// the next port that satisfies the step's condition", and the conditions are stated
/// in the machine's logical call depth.</para>
/// </summary>
public class Adr035SteppingTests
{
    private readonly ITestOutputHelper _log;

    public Adr035SteppingTests(ITestOutputHelper log) => _log = log;

    /// <summary>
    /// One stop site per line, so a line number names exactly one port:
    /// <code>
    ///   2: top(X) :-          clause entry of top/1
    ///   3:     mid(X),        the goal mid(X)
    ///   4:     tail(X).       the goal tail(X)
    ///   5: mid(X) :-          clause entry of mid/1
    ///   6:     leaf(X).       the goal leaf(X)
    ///   7: leaf(7).           clause entry of leaf/1
    ///   8: tail(7).           clause entry of tail/1
    /// </code>
    /// </summary>
    private const string Nested =
        "top(X) :-\n    mid(X),\n    tail(X).\nmid(X) :-\n    leaf(X).\nleaf(7).\ntail(7).\n";

    private static PrologEngine DebugEngine(string program, bool lco = false)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        if (!lco)
        {
            // Last-call optimisation reclaims a predicate's frame BEFORE its final
            // goal runs, so under it the predicate has no exit port and no stack frame
            // left to show. Debuggers turn it off; that is what debug_lco is for.
            engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        }
        return engine;
    }

    /// <summary>Runs <paramref name="goal"/> under a debug session, applying
    /// <paramref name="steps"/> in order — one per stop. A stop with no step left
    /// resumes with Continue.</summary>
    private List<DebugStopEvent> Walk(
        PrologEngine engine, string goal, params StepMode[] steps)
    {
        var stops = new List<DebugStopEvent>();
        int next = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            stops.Add(e);
            if (next < steps.Length) s.Resume(steps[next++]);
        });
        engine.AttachDebugSession(svc);
        engine.QueryAll(goal).ToList();
        engine.AttachDebugSession(null);

        foreach (var s in stops)
            _log.WriteLine($"{s.Reason,-10} {s.Goal,-10} depth={s.Depth} {s.File}:{s.Line}");
        return stops;
    }

    private static string[] Ports(IEnumerable<DebugStopEvent> stops) =>
        stops.Select(s => $"{s.Reason} {s.Goal}").ToArray();

    // ---------- stepping ----------

    [Fact]
    public void StepInto_TakesTheNextPort_HoweverDeep()
    {
        var engine = DebugEngine(Nested);
        // Line 2 is `top(X) :-` — a head, whose "clause entered" point IS its first
        // goal's. So it snaps forward to line 3, the call to mid/1, and stops there.
        Assert.Equal(3, engine.BoundLine("<string>", 2));
        engine.AddBreakpoint("<string>", 2);

        var stops = Walk(engine, "top(A).",
            StepMode.Into, StepMode.Into, StepMode.Into, StepMode.Into);

        // Down into both calls, then back up through both exits: the ports of a nested
        // deterministic call, in the order the machine reaches them.
        Assert.Equal(new[]
        {
            "Breakpoint top/1",   // stopped in top/1, about to call mid/1 (line 3)
            "Call leaf/1",        // into mid/1, which calls leaf/1
            "Exit leaf/1",
            "Exit mid/1",
            "Call tail/1",        // back in top/1, on to its second goal
        }, Ports(stops));

        // Into mid/1 is a level deeper; into leaf/1 deeper still; then back out.
        Assert.Equal(new[] { 0, 1, 1, 0, 0 },
            stops.Select(s => s.Depth - stops[0].Depth));
    }

    [Fact]
    public void StepInto_LandsInsideTheGoalOnTheBreakpointLine()
    {
        var engine = DebugEngine(Nested);
        engine.AddBreakpoint("<string>", 3);   // the goal mid(X), inside top/1

        var stops = Walk(engine, "top(A).", StepMode.Into);

        // The breakpoint IS the call port of mid/1 — reporting the call again would
        // stop the user twice on one line, moving nothing. So a step into from here
        // lands inside mid/1, which is what "into" means.
        Assert.Equal(new[] { "Breakpoint top/1", "Call leaf/1" }, Ports(stops));
        Assert.Equal(6, stops[1].Line);
    }

    [Fact]
    public void StepOver_SkipsWhatTheGoalDoesInside_AndLandsOnItsExit()
    {
        var engine = DebugEngine(Nested);
        engine.AddBreakpoint("<string>", 3);   // the goal mid(X)

        var stops = Walk(engine, "top(A).", StepMode.Over, StepMode.Over);

        // Nothing inside mid/1 — no leaf/1 port — but mid/1's own exit is not skipped:
        // in a port model there is no depth that separates "mid exited" from "tail is
        // called", because they are siblings. So a step over lands on the exit port of
        // the goal stepped over, as it does in SWI. The step after that reaches tail/1.
        Assert.Equal(new[]
        {
            "Breakpoint top/1",
            "Exit mid/1",
            "Call tail/1",
        }, Ports(stops));
        Assert.Equal(4, stops[2].Line);
        Assert.DoesNotContain(stops, s => s.Goal == "leaf/1");
    }

    [Fact]
    public void StepOut_RunsToTheEndOfTheGoalWeAreIn()
    {
        var engine = DebugEngine(Nested);
        engine.AddBreakpoint("<string>", 6);   // the goal leaf(X), inside mid/1

        var stops = Walk(engine, "top(A).", StepMode.Out);

        // Out of leaf/1 entirely: not its exit (that is still leaf's depth), but the
        // exit of the predicate that called it.
        Assert.Equal(new[] { "Breakpoint mid/1", "Exit mid/1" }, Ports(stops));
        Assert.True(stops[1].Depth < stops[0].Depth,
            $"stepping out must surface: depth went {stops[0].Depth} -> {stops[1].Depth}");
    }

    [Fact]
    public void Continue_RunsToTheNextBreakpoint()
    {
        //   2: p(a).
        //   3: p(b).
        //   4: p(c).
        var engine = DebugEngine("p(a).\np(b).\np(c).\n");
        engine.AddBreakpoint("<string>", 3);

        var stops = Walk(engine, "p(X).");   // no steps: every stop resumes with Continue

        Assert.Single(stops);
        Assert.Equal(StopReason.Breakpoint, stops[0].Reason);
        Assert.Equal(3, stops[0].Line);
    }

    // ---------- the two ports a frame-based debugger cannot express ----------

    [Fact]
    public void TheRedoPortIsReached_WhenAGoalIsRetriedForAnotherSolution()
    {
        //   2: p(1).
        //   3: p(2).
        //   4: t :-
        //   5:     p(X),
        //   6:     X > 1.
        var engine = DebugEngine("p(1).\np(2).\nt :-\n    p(X),\n    X > 1.\n");
        engine.AddBreakpoint("<string>", 5);   // the goal p(X)

        var stops = Walk(engine, "t.", StepMode.Into, StepMode.Into, StepMode.Into);

        // p/1 succeeds with 1, the guard X > 1 fails, and p/1 is REDONE for 2. There
        // is no return address to step over here — the machine is going BACKWARDS into
        // a goal that had already succeeded. Only a port model can say this.
        Assert.Contains(stops, s => s.Reason == StopReason.Redo);
        Assert.Equal("Exit p/1", Ports(stops)[1]);
        Assert.Equal("Redo p/1", Ports(stops)[2]);
        // And it names the clause about to be retried — p(2), on line 3 — not the
        // guard that failed, which is where the machine happens to be standing.
        Assert.Equal(3, stops[2].Line);
        Assert.Equal("p/1", $"{stops[2].Frames[0].Name}/{stops[2].Frames[0].Arity}");
    }

    [Fact]
    public void TheFailPortIsReached_WhenAGoalRunsOutOfSolutions()
    {
        //   2: p(1).
        //   3: t :-
        //   4:     p(9).
        var engine = DebugEngine("p(1).\nt :-\n    p(9).\n");
        engine.AddBreakpoint("<string>", 4);   // the goal p(9)

        var stops = Walk(engine, "t.", StepMode.Into, StepMode.Into);

        Assert.Contains(stops, s => s.Reason == StopReason.Fail);
    }

    // ---------- frames ----------

    [Fact]
    public void TheCallStackIsRecomposedFromTheEnvironmentChain()
    {
        var engine = DebugEngine(Nested);
        engine.AddBreakpoint("<string>", 7);   // inside leaf/1

        var frames = Walk(engine, "top(A).")[0].Frames;
        _log.WriteLine("frames: " + string.Join(" <- ",
            frames.Select(f => $"{f.Name}/{f.Arity} at {f.File}:{f.Line}")));

        // Innermost first: leaf, called by mid, called by top — and under all of it the
        // top-level query itself (`?-`, arity -1: it is not a predicate), because that is
        // where the user launched this from and where they are still standing. The C# stack
        // knows none of this — Tier-0 runs the whole program inside a single Dispatch frame —
        // so it can only have come from the machine's own environment chain.
        Assert.Equal(new[] { "leaf/1", "mid/1", "top/1", "?-/-1" },
            frames.Select(f => $"{f.Name}/{f.Arity}"));
    }

    [Fact]
    public void EachFrameCarriesTheSourceLineItIsStoppedOn()
    {
        var engine = DebugEngine(Nested);
        engine.AddBreakpoint("<string>", 7);

        var frames = Walk(engine, "top(A).")[0].Frames;

        Assert.Equal(7, frames[0].Line);            // stopped at leaf/1's clause, line 7
        Assert.Equal(6, frames[1].Line);            // mid/1 is waiting on leaf(X), line 6
        Assert.Equal(3, frames[2].Line);            // top/1 is waiting on mid(X), line 3
        Assert.All(frames, f => Assert.Equal("<string>", f.File));
    }

    [Fact]
    public void WithLastCallOptimisationOn_TheFramesWithNothingLeftToDoAreGone()
    {
        // The honest statement of the trade-off, and the reason debug_lco exists.
        var lco = DebugEngine(Nested, lco: true);
        lco.AddBreakpoint("<string>", 7);
        var withLco = Walk(lco, "top(A).")[0].Frames;

        var noLco = DebugEngine(Nested, lco: false);
        noLco.AddBreakpoint("<string>", 7);
        var withoutLco = Walk(noLco, "top(A).")[0].Frames;

        // mid/1 called leaf/1 as its LAST goal, so LCO reclaimed mid's frame before
        // leaf ran: by the time we are stopped in leaf, the machine has genuinely
        // forgotten mid — there is nothing left to show. top/1 survives, because it
        // still has tail(X) to run and so kept its frame. The debugger can only show
        // what the machine still has; turning LCO off is what makes it keep it all.
        // (The query's own frame goes the same way, and for the same reason: its last goal
        // was top(A), so LCO reclaimed it before top ran. With LCO off it is there — the
        // bottom of the stack, where the user launched this from.)
        Assert.Equal(new[] { "leaf/1", "top/1" },
            withLco.Select(f => $"{f.Name}/{f.Arity}"));
        Assert.Equal(new[] { "leaf/1", "mid/1", "top/1", "?-/-1" },
            withoutLco.Select(f => $"{f.Name}/{f.Arity}"));
    }

    // ---------- the model, pinned ----------

    [Fact]
    public void ABreakpointOnALineWithNoCodeOfItsOwnSnapsForward()
    {
        //   2: top(X) :-
        //   3:
        //   4:     % nothing here either
        //   5:     mid(X).
        //   6: mid(7).
        var engine = DebugEngine(
            "top(X) :-\n\n    % nothing here either\n    mid(X).\nmid(7).\n");

        // None of lines 2, 3, 4 is a place the machine can stop: 3 and 4 have no code,
        // and 2 is a head, whose entry point IS the first goal's. All three snap to 5.
        Assert.Equal(5, engine.BoundLine("<string>", 2));
        Assert.Equal(5, engine.BoundLine("<string>", 3));
        Assert.Equal(5, engine.BoundLine("<string>", 4));

        Assert.Equal(1, engine.AddBreakpoint("<string>", 3));
        var stops = Walk(engine, "top(A).");
        Assert.Single(stops);
        Assert.Equal(5, stops[0].Line);
    }

    [Fact]
    public void ABreakpointPastTheEndOfTheCodeDoesNotBind()
    {
        // A hollow breakpoint: there is nothing at or after this line to stop in, and
        // saying so is better than pretending it took.
        var engine = DebugEngine("top(X) :-\n    mid(X).\nmid(7).\n");

        Assert.Equal(-1, engine.BoundLine("<string>", 99));
        Assert.Equal(0, engine.AddBreakpoint("<string>", 99));
        Assert.Empty(engine.Breakpoints);
    }

    [Fact]
    public void TwoProgramsWithIdenticalCodeOnDifferentLinesDoNotShareEachOthersPositions()
    {
        // The static link is cached process-wide, keyed by a hash of the BYTECODE — so
        // that a pool loading one bundle N times links it once. These two programs are
        // byte-identical and written on entirely different lines, which is exactly the
        // collision: without the debug metadata in that key, the second engine gets the
        // first one's link, and reports its neighbour's source positions. A debugger
        // showing the wrong lines, with no error anywhere.
        var first = DebugEngine("q(X) :-\n    r(X).\nr(1).\n");                     // goal on 3
        var second = DebugEngine("\n\n\n\nq(X) :-\n    r(X).\nr(1).\n");            // goal on 7

        Assert.Equal(3, first.BoundLine("<string>", 3));
        Assert.Equal(7, second.BoundLine("<string>", 7));

        second.AddBreakpoint("<string>", 7);
        var stops = Walk(second, "q(A).");

        Assert.Single(stops);
        Assert.Equal(7, stops[0].Line);
    }

    [Fact]
    public void SteppingDoesNotChangeWhatTheProgramComputes()
    {
        var engine = DebugEngine("app([], L, L).\napp([H|T], L, [H|R]) :-\n    app(T, L, R).\n");
        engine.AddBreakpoint("<string>", 4);

        var svc = new DebugService(engine, (s, e) => s.Resume(StepMode.Into));
        engine.AttachDebugSession(svc);
        var result = engine.QueryFirst<List<int>>("app([1,2], [3], X).", "X");
        engine.AttachDebugSession(null);

        Assert.Equal(new[] { 1, 2, 3 }, result);
    }
}
