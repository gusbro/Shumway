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

        // A step lands on a GOAL — the next thing the program is about to do — and, going
        // down, that is the first goal of the clause we stepped into. Coming back up, the
        // exit of an ENCLOSING clause is a place the user asked to see (their clause is
        // finished); the exit of the goal they just stepped through is not, because the next
        // goal's call port says the same thing and points at a line they wrote. leaf/1 is a
        // fact — there is nothing inside it to step into — so the step surfaces at mid/1's
        // end rather than stopping on leaf's own `proceed`.
        Assert.Equal(new[]
        {
            "Breakpoint top/1",   // stopped in top/1, about to call mid/1 (line 3)
            "Call leaf/1",        // into mid/1, which calls leaf/1
            "Exit mid/1",         // leaf/1 is a fact: mid/1 is done
            "Call tail/1",        // back in top/1, on to its second goal
            "Exit top/1",
        }, Ports(stops));
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
    public void StepOver_LandsOnTheNextGoalOfTheClause_NotOnTheEndOfSomeOtherOne()
    {
        var engine = DebugEngine(Nested);
        engine.AddBreakpoint("<string>", 3);   // the goal mid(X)

        var stops = Walk(engine, "top(A).", StepMode.Over, StepMode.Over);

        // Step over mid(X) and you are on tail(X) — the next goal of the clause you are
        // stepping through, on the line you are looking at.
        //
        // It used to land on mid/1's EXIT port, which is where a port tracer would put you.
        // But an exit port fires with the machine standing in the CALLEE, at its `proceed`,
        // so the caret jumped to the last line of whichever clause of mid/1 happened to
        // succeed. "Step over and it stops at the end of some other clause" was the report,
        // and it was exactly what the model said to do.
        Assert.Equal(new[]
        {
            "Breakpoint top/1",
            "Call tail/1",     // line 4 — the next goal, in the clause we are stepping
            "Exit top/1",      // and then this clause is done
        }, Ports(stops));
        Assert.Equal(4, stops[1].Line);
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

        // p/1's EXIT is not stopped at — a step lands on the next GOAL, and the exit port
        // would put the caret on p(1)'s clause, which is not where the program is going. The
        // guard `X > 1` compiles inline (ADR-018: no call, no port), so the next thing that
        // happens to this program IS the redo.
        Assert.Equal("Redo p/1", Ports(stops)[1]);
        // And it names the clause about to be retried — p(2), on line 3 — not the
        // guard that failed, which is where the machine happens to be standing.
        Assert.Equal(3, stops[1].Line);
        Assert.Equal("p/1", $"{stops[1].Frames[0].Name}/{stops[1].Frames[0].Arity}");
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
        // The query frame names the goal the user typed — `?- top(A)`, not a bare `?-`,
        // which would say only "a query is running" to someone who is stopped in one.
        Assert.Equal(new[] { "leaf/1", "mid/1", "top/1", "?- top(A)/-1" },
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
        Assert.Equal(new[] { "leaf/1", "mid/1", "top/1", "?- top(A)/-1" },
            withoutLco.Select(f => $"{f.Name}/{f.Arity}"));
    }

    [Fact]
    public void ARecursivePredicateShowsOneFramePerLevel()
    {
        // It did not. Every frame of a recursive predicate stores the SAME return address —
        // the instruction after the recursive call — and the env-chain walk was dropping any
        // frame whose return address matched the one it started from, meaning ALL of them. A
        // 12-deep recursion showed TWO frames, and the debugger then read the query's
        // variables out of whichever frame it had landed on instead: it reported the answer
        // as a loop counter, confidently. The duplicate is only ever the FIRST frame (the
        // running clause saved the current cp into its own environment at `allocate`), and
        // that is the only one to skip.
        var engine = DebugEngine(
            "down(0) :- mark(bottom).\n"
            + "down(N) :- N > 0, M is N - 1, down(M).\n"
            + "mark(_).\n", lco: false);
        engine.AddBreakpoint("<string>", 2);   // the bottom of the recursion, twelve levels down

        var frames = Walk(engine, "down(12).")[0].Frames;
        _log.WriteLine("frames: " + string.Join(" <- ", frames.Select(f => $"{f.Name}/{f.Arity}")));

        // down(0), the twelve down/1 frames above it, and under all of it the query.
        Assert.Equal(13, frames.Count(f => f.Name == "down"));
        Assert.StartsWith("?-", frames[^1].Name);

        // And each level holds ITS OWN N — which is the whole point of a stack. (down(0)
        // matched the first clause, which has no N: twelve values, innermost first.)
        var ns = frames.Where(f => f.Name == "down")
                       .SelectMany(f => f.Variables.Where(v => v.Name == "N"))
                       .Select(v => v.Value)
                       .ToArray();
        Assert.Equal(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12" }, ns);
    }

    [Fact]
    public void SteppingPastTheEndOfTheQuery_AbandonsTheStep_InsteadOfLeavingItInFlight()
    {
        // The user's report, reduced: a query with a choice point in it, stopped in the
        // middle, then F10 F10. The second one steps past the last goal -- the query
        // SUCCEEDS, hands its answer back, and stands still waiting to be asked for another.
        // No port is ever coming, so the step can never be satisfied.
        //
        // Visual Studio waited for it forever: it believed the program was still running, and
        // answered every key after that with "Unable to step. Operation not supported." The
        // engine now says the step is over, and the debugger cancels it. There is no honest
        // alternative -- stopping the user in the host's C# would be showing them a place
        // they did not ask to see, and where their program is not.
        var engine = DebugEngine(
            "pick(X) :- member(X, [a,b,c]).\n"
            + "shout(X) :- writeln(X).\n", lco: false);
        engine.AddBreakpoint("<string>", 3);   // shout/1's body

        var stops = Walk(engine, "pick(X), shout(X).",
            StepMode.Over, StepMode.Over, StepMode.Over);

        // Stopped in shout/1; step over its body; step over its exit -- which lands on the
        // exit port of the QUERY, the last port there is. Step again and the query is done:
        // it hands back X = a and stands still. That third step has nowhere to land, and the
        // engine says so -- not with a stop to look at (there is no stack: the machine is not
        // in the program), with a message.
        Assert.Equal(
            new[] { StopReason.Breakpoint, StopReason.Exit, StopReason.Exit, StopReason.StepAbandoned },
            stops.Take(4).Select(s => s.Reason));
        Assert.Empty(stops[3].Frames);

        // Once per departure -- and once only. (The walk goes on to enumerate the other
        // solutions, and the breakpoint fires again on each: a step is over, the breakpoints
        // are not.)
        Assert.Single(stops, s => s.Reason == StopReason.StepAbandoned);
    }

    [Fact]
    public void TheQueryIsOnTheStackExactlyOnce()
    {
        // It was on there TWICE. Past the query's own frame lies the address it RETURNS to --
        // the top level's code, which no Prolog frame describes -- and the search that names
        // an address takes the last predicate at or before it, so that address came back
        // named `__query__` as well. The user saw their query twice, the second copy with no
        // variables. A query is not recursive: once it is on the stack, the walk is over.
        var engine = DebugEngine(Nested, lco: false);
        engine.AddBreakpoint("<string>", 7);

        var frames = Walk(engine, "top(A).")[0].Frames;
        Assert.Single(frames, f => f.Name.StartsWith("?-"));
        Assert.StartsWith("?-", frames[^1].Name);   // and it is the BOTTOM
    }

    [Fact]
    public void SteppingStaysInTheUsersProgram_AndDoesNotWanderIntoTheLibrary()
    {
        // What the user actually hit. Their query calls member/2 -- the PRELUDE's -- and the
        // top level wraps every query in a copy_term/3 of its own. Both are
        // `:- disable_debug`, and both are compiled that way; but a PORT is raised by the
        // interpreter at every call and every proceed regardless of what the code was
        // compiled from, so a step honoured them. Two F10s and the user was standing in
        // `copy_term/3`, then in `$prelude$$attr_goals_of/2`: code they did not write, cannot
        // open, and did not ask to step through.
        //   2: pick(X) :-
        //   3:     member(X, [a,b,c]).
        //   4: shout(X) :-
        //   5:     writeln(X).
        //   6: go(X) :-
        //   7:     pick(X),
        //   8:     shout(X).
        var engine = DebugEngine(
            "pick(X) :-\n    member(X, [a,b,c]).\n"
            + "shout(X) :-\n    writeln(X).\n"
            + "go(X) :-\n    pick(X),\n    shout(X).\n", lco: false);
        engine.AddBreakpoint("<string>", 3);   // pick/1's body: the call to member/2

        var stops = Walk(engine, "go(A).",
            StepMode.Into, StepMode.Into, StepMode.Into, StepMode.Into, StepMode.Into);

        // Every stop names a predicate of theirs (or their query). member/2 runs -- it just
        // does not stop anyone; control comes back to the user's program at pick/1's caller,
        // and THAT is where the step lands.
        foreach (var s in stops)
            Assert.DoesNotContain("$prelude$", s.Goal);
        Assert.DoesNotContain(stops, s => s.Goal.StartsWith("member/"));
        Assert.DoesNotContain(stops, s => s.Goal.StartsWith("copy_term/"));

        // Stopped inside pick/1 (on the member/2 goal it wrote), and five steps later the
        // user is still walking their own three predicates: shout/1, and the writeln/1 goal
        // inside it -- a builtin the user wrote, which is a goal like any other.
        Assert.Equal("pick/1", stops[0].Goal);
        Assert.Contains(stops, s => s.Goal == "shout/1");
        Assert.Contains(stops, s => s.Goal == "writeln/1");
    }

    [Fact]
    public void AVariableWhoseTurnHasNotComeShowsAsUnbound_NotAsAnError()
    {
        // NO STOP RENDERS A VARIABLE AS AN ERROR. `allocate` does not touch the Y slots -- a
        // permanent is written at its FIRST occurrence, and running code never reads one
        // before -- so a slot the machine has not reached yet holds a control word, not a
        // term. Debug codegen initialises the ones it knows the names of, precisely so a
        // debugger never reads garbage; anything the walk reaches that is NOT a term (a
        // control word, an internal cell) is a variable with no value yet, which is what an
        // unbound variable IS, and it shows as one.
        //
        // It used to let the materializer throw and caught the exception -- which works, and
        // is LOUD: a caught exception is invisible from outside and a line in the Output
        // window from inside Visual Studio. Every Break All printed "Exception thrown:
        // 'System.NotSupportedException' in Shumway.Embedding.dll", which is not an error and
        // reads exactly like one.
        //
        //   2: p(X) :-
        //   3:     q(X),          <- stopped here; r/1's argument has not been built
        //   4:     r(_Later).
        var engine = DebugEngine(
            "p(X) :-\n    q(X),\n    r(_Later).\nq(_).\nr(_).\ngo :-\n    p(1).\n",
            lco: false);
        engine.AddBreakpoint("<string>", 3);

        var stop = Walk(engine, "go.")[0];
        foreach (var f in stop.Frames)
            _log.WriteLine($"  {f.Name}/{f.Arity}: "
                + string.Join(", ", f.Variables.Select(v => $"{v.Name} = {v.Value}")));

        Assert.Equal("p/1", stop.Goal);
        Assert.Equal("1", stop.Variables.First(v => v.Name == "X").Value);
        foreach (var f in stop.Frames)
            Assert.DoesNotContain(f.Variables, v => v.Value.Contains("unavailable"));
    }

    [Fact]
    public void StepOverDoesNotStopInsideTheGoalItIsSkipping_WhenThatGoalBacktracks()
    {
        // THE REPORT: "F10 y en vez de irme parando en cada subgoal me para en la salida de
        // cada subgoal previo." Stepping over a goal that tries a second clause stopped at the
        // REDO port of that goal -- on a line in the middle of the predicate the user had just
        // said to skip.
        //
        // A callee's frame is not allocated until its clause runs, so retrying a clause of it
        // reads at exactly the depth of the CALL that started it. Depth alone cannot tell "the
        // goal I skipped is trying another clause" from "the clause I am in has moved on" --
        // but the reason can: a redo at the step's own depth is always inside the goal that was
        // skipped, so a step over honours a redo only from an ENCLOSING goal. Which is the rule
        // the exit port already followed.
        //
        //   2: pick(X) :-
        //   3:     choose(X),      <- stopped here; choose/1 has two clauses
        //   4:     check(X).
        //   5: choose(1).
        //   6: choose(2).
        //   7: check(2).
        var engine = DebugEngine(
            "pick(X) :-\n    choose(X),\n    check(X).\nchoose(1).\nchoose(2).\ncheck(2).\n",
            lco: false);
        engine.AddBreakpoint("<string>", 3);

        var stops = Walk(engine, "pick(A).", StepMode.Over, StepMode.Over, StepMode.Over);

        // choose(1) succeeds, check(1) FAILS, choose/1 is retried with its second clause -- all
        // of it inside the goal the user stepped over. What they see is the next goal of their
        // clause, and then their clause failing or ending, never the inside of choose/1.
        Assert.DoesNotContain(stops, s => s.Reason == StopReason.Redo);
        Assert.DoesNotContain(stops, s => s.Goal == "choose/1" && s.Reason != StopReason.Call);
        Assert.Contains(stops, s => s.Goal == "check/1" && s.Reason == StopReason.Call);
    }

    [Fact]
    public void ADeepStackShowsBothEnds_AndSaysHowMuchOfTheMiddleItLeftOut()
    {
        // Nobody reads three hundred frames of the same clause. What a user reads is the top
        // (where the machine is) and the bottom (how it got in) -- and the middle is a
        // recursion, which they can see is a recursion from the two frames of it either side.
        // So the stack shows both ends and SAYS how many frames it left out, rather than
        // running to a length no window can show and no buffer can carry.
        var engine = DebugEngine(
            "down(0) :-\n    !,\n    bottom.\ndown(N) :-\n    N1 is N - 1,\n    down(N1).\nbottom.\n",
            lco: false);
        engine.AddBreakpoint("<string>", 4);   // bottom/0, 300 frames down

        var frames = Walk(engine, "down(300).")[0].Frames;
        _log.WriteLine($"{frames.Count} frames");
        foreach (var f in frames.Take(2).Concat(frames.Skip(78).Take(4)))
            _log.WriteLine($"  {f.Name}/{f.Arity} :{f.Line}");

        // 80 innermost, the sentence, 20 outermost -- and the whole recursion is there in the
        // count, not thrown away in silence.
        Assert.Equal(101, frames.Count);
        var omitted = frames[80];
        Assert.Matches(@"^\.\.\. [\d,\.]+ frames omitted \.\.\.$", omitted.Name);
        Assert.True(omitted.Arity < 0);            // not a predicate: rendered without an arity
        Assert.Empty(omitted.Variables);

        // The ends are real frames, with their variables (the innermost down/1 is the one that
        // reached zero), and the bottom of the stack is still the query.
        Assert.Equal("... 202 frames omitted ...", omitted.Name);
        Assert.Equal("down/1", $"{frames[0].Name}/{frames[0].Arity}");
        Assert.Equal(4, frames[0].Line);                    // stopped on the call to bottom/0
        Assert.Equal("down/1", $"{frames[^2].Name}/{frames[^2].Arity}");
        Assert.StartsWith("?-", frames[^1].Name);
        Assert.Equal("300", frames[^2].Variables.First(v => v.Name == "N").Value);
    }

    [Fact]
    public void AQueryNobodyIsSteppingThrough_SaysNothingWhenItEnds()
    {
        // The other half: the message exists for a debugger waiting on a step. A program
        // running under a session with nobody stepping must produce no stop at all -- an
        // engine that announced the end of every query would stop the user's world on every
        // solution of every goal they ever ran.
        var engine = DebugEngine(Nested, lco: false);

        var stops = new List<DebugStopEvent>();
        var svc = new DebugService(engine, (s, e) => stops.Add(e));
        engine.AttachDebugSession(svc);
        engine.QueryAll("top(A).").ToList();
        engine.AttachDebugSession(null);

        Assert.Empty(stops);
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
