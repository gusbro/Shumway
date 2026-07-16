using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 — the Immediate window: evaluate any goal against the live engine, from a
/// stop, with the stopped frame's variables substituted by their current values.
///
/// <para>The goal runs as a REAL query — a new activation over the same engine, the same
/// database — which is the semantics asked for: an <c>assertz</c> persists exactly as it
/// would from any nested mid-query activation, and the suspended query's own view of the
/// database follows the ordinary logical-update rules. The evaluation brackets everything
/// it clobbers (the per-query debug tables, the channel snapshot, the service's own
/// step state) and puts it back, stop or no stop.</para>
/// </summary>
public class Adr035EvaluateTests
{
    private readonly ITestOutputHelper _log;

    public Adr035EvaluateTests(ITestOutputHelper log) => _log = log;

    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    //   2: p(X) :-
    //   3:     q(X, Y),
    //   4:     r(Y).
    //   5: q(N, out(N)).
    //   6: r(_).
    //   7: double(A, B) :- B is A * 2.
    private const string Program =
        "p(X) :-\n    q(X, Y),\n    r(Y).\nq(N, out(N)).\nr(_).\n"
        + "double(A, B) :- B is A * 2.\n";

    /// <summary>Stops at the breakpoint, runs <paramref name="goal"/> from the innermost
    /// frame, and returns (evaluation result, everything else the run produced).</summary>
    private (string Result, List<DebugStopEvent> Stops) EvalAtBreak(
        PrologEngine engine, string query, string goal, int frameIndex = 0)
    {
        string result = "";
        var stops = new List<DebugStopEvent>();
        bool evaluated = false;
        var svc = new DebugService(engine, (s, e) =>
        {
            stops.Add(e);
            if (!evaluated)
            {
                evaluated = true;
                result = s.EvaluateGoal(frameIndex, goal);
            }
        });
        engine.AttachDebugSession(svc);
        engine.QueryAll(query).ToList();
        engine.AttachDebugSession(null);
        _log.WriteLine($"eval: {result}");
        return (result, stops);
    }

    [Fact]
    public void AGoalRunsWithTheFrameVariablesSubstituted()
    {
        var engine = DebugEngine(Program);
        engine.AddBreakpoint("<string>", 4);   // inside p/1: X and Y are bound

        // `X` here is the FRAME's X (bound to 7 by the query below); B is the goal's own,
        // free, and comes back as the answer.
        var (result, _) = EvalAtBreak(engine, "p(7).", "double(X, B)");
        Assert.Equal("B = 14", result);
    }

    [Fact]
    public void AGoalWithNoFreeVariablesAnswersTrueOrFalse()
    {
        var engine = DebugEngine(Program);
        engine.AddBreakpoint("<string>", 4);

        Assert.Equal("true", EvalAtBreak(engine, "p(7).", "q(7, out(7))").Result);
        Assert.Equal("false", EvalAtBreak(engine, "p(7).", "q(7, out(8))").Result);
    }

    [Fact]
    public void CompoundValuesSubstituteWhole()
    {
        var engine = DebugEngine(Program);
        engine.AddBreakpoint("<string>", 4);   // Y = out(7)

        var (result, _) = EvalAtBreak(engine, "p(7).", "Y = out(Inner)");
        Assert.Equal("Inner = 7", result);
    }

    [Fact]
    public void TheDatabaseIsTheLiveOne_AnAssertzPersists()
    {
        // The point of running in a real activation over the live engine: the same
        // semantics as any nested mid-query activation. The assertz lands in the real
        // database — visible to a second evaluation, and to the program after the stop.
        var engine = DebugEngine(Program + ":- dynamic seen/1.\n");
        engine.AddBreakpoint("<string>", 4);

        string first = "", second = "";
        int hits = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            if (++hits > 1) return;
            first = s.EvaluateGoal(0, "assertz(seen(X))");   // X = 7, the frame's
            second = s.EvaluateGoal(0, "seen(Q)");
        });
        engine.AttachDebugSession(svc);
        engine.QueryAll("p(7).").ToList();
        engine.AttachDebugSession(null);

        Assert.Equal("true", first);
        Assert.Equal("Q = 7", second);

        // And it survives the query: the program's world really changed.
        Assert.Single(engine.QueryAll("seen(V).").ToList());
    }

    [Fact]
    public void TheSuspendedQueryResumesUndamaged()
    {
        // The bracket. The eval runs a full query setup, which rebuilds the per-query
        // debug tables — and the SUSPENDED query still needs its own to finish stepping.
        // After an eval (with an assertz in it, for good measure), the original query
        // steps on and completes with the right answer.
        var engine = DebugEngine(Program + ":- dynamic seen/1.\n");
        engine.AddBreakpoint("<string>", 3);   // the call to q/2; r/1 is the next goal

        var stops = new List<DebugStopEvent>();
        bool evaluated = false;
        var svc = new DebugService(engine, (s, e) =>
        {
            stops.Add(e);
            if (!evaluated)
            {
                evaluated = true;
                s.EvaluateGoal(0, "assertz(seen(X))");
                s.Resume(StepMode.Over);   // and STEP, from the stop we were at
            }
        });
        engine.AttachDebugSession(svc);
        var solutions = engine.QueryAll("p(7).").ToList();
        engine.AttachDebugSession(null);

        foreach (var s in stops)
            _log.WriteLine($"{s.Reason,-10} {s.Goal,-8} line={s.Line} frames={s.Frames.Count}");

        // The query succeeded, the step after the eval landed on the next goal of the
        // clause (r/1, line 4 -> its call), and the stack at that stop is the ORIGINAL
        // query's, boundary-free, down to `?- p(7)`.
        Assert.Single(solutions);
        Assert.Equal(2, stops.Count);
        Assert.Equal(StopReason.Call, stops[1].Reason);
        Assert.Equal("r/1", stops[1].Goal);
        Assert.StartsWith("?-", stops[1].Frames[^1].Name);
        Assert.DoesNotContain(stops[1].Frames, f => f.Name.StartsWith("[Immediate"));
    }

    [Fact]
    public void ABreakpointReachedByTheEvaluatedGoalStops_WithBothStacksShown()
    {
        // The C#-parity behavior, and better: the nested stop's stack is the evaluated
        // goal's frames, a boundary naming the evaluation, and UNDER it the suspended
        // query the user was stopped in — not an opaque cut.
        var engine = DebugEngine(Program);
        engine.AddBreakpoint("<string>", 4);   // hit by p(7) AND by the evaluated p(1)

        string result = "";
        var stops = new List<DebugStopEvent>();
        bool evaluated = false;
        var svc = new DebugService(engine, (s, e) =>
        {
            stops.Add(e);
            if (!evaluated)
            {
                evaluated = true;
                result = s.EvaluateGoal(0, "p(1)");
            }
        });
        engine.AttachDebugSession(svc);
        engine.QueryAll("p(7).").ToList();
        engine.AttachDebugSession(null);

        foreach (var f in stops[1].Frames)
            _log.WriteLine($"  {f.Name}{f.HeadArgs}");

        // The eval completed (the nested stop's handler resumed with Continue).
        Assert.Equal("true", result);

        // The nested stop happened, and its stack is MIXED: p(1)'s frames, the boundary,
        // then the suspended p(7)'s frames down to the query.
        Assert.Equal(2, stops.Count);   // the outer breakpoint, then the eval's
        var nested = stops[1];
        Assert.Equal(StopReason.Breakpoint, nested.Reason);
        Assert.Equal("(1)", nested.Frames[0].HeadArgs);
        int boundary = nested.Frames.ToList().FindIndex(f => f.Name.StartsWith("[Immediate: p(1)"));
        Assert.True(boundary > 0, "no [Immediate] boundary frame");
        Assert.Equal("(7)", nested.Frames[boundary + 1].HeadArgs);
        Assert.StartsWith("?-", nested.Frames[^1].Name);
    }

    [Fact]
    public void SuppressedMode_RunsStraightThroughBreakpoints()
    {
        var engine = DebugEngine(Program);
        engine.AddBreakpoint("<string>", 4);

        DebugService.SuppressStopsDuringEvaluation = true;
        try
        {
            var (result, stops) = EvalAtBreak(engine, "p(7).", "p(1)");
            Assert.Equal("true", result);
            Assert.Single(stops);   // only the outer breakpoint; the eval's was ignored
        }
        finally
        {
            DebugService.SuppressStopsDuringEvaluation = false;
        }
    }

    [Fact]
    public void ARunawayGoalTimesOut_InsteadOfHangingTheDebugger()
    {
        var engine = DebugEngine("spin :- spin.\n" + Program);
        engine.AddBreakpoint("<string>", 5);   // inside q/2

        var saved = DebugService.EvaluationTimeout;
        DebugService.EvaluationTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var (result, _) = EvalAtBreak(engine, "p(7).", "spin");
            _log.WriteLine(result);
            Assert.Contains("timed out", result);
        }
        finally
        {
            DebugService.EvaluationTimeout = saved;
        }
    }

    [Fact]
    public void ASyntaxErrorIsAnAnswer_NotAnException()
    {
        var engine = DebugEngine(Program);
        engine.AddBreakpoint("<string>", 4);

        var (result, _) = EvalAtBreak(engine, "p(7).", "q(X,");
        Assert.StartsWith("syntax error:", result);
    }

    [Fact]
    public void ABreakpointSetWhileStopped_IsArmedForTheEvaluation()
    {
        // The bug the user hit: in BREAK state nothing drains the command channel until the
        // engine resumes — the engine thread is parked inside the notify — so a breakpoint
        // drawn with F9 WHILE STOPPED sits unread, and an Immediate-window evaluation runs
        // straight past it. A breakpoint set BEFORE the stop is already armed and does stop.
        // The evaluation now drains and applies the pending breakpoint FIRST, so the two
        // cases are the same. This test goes through the real channel, because that is where
        // the pending command lives.
        var engine = DebugEngine(Program);
        engine.AddBreakpoint("<string>", 4);          // the OUTER stop, inside p(7)

        ChannelDebugSession? session = null;
        var stopLines = new List<int>();
        string evalResult = "";
        bool acted = false;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            stopLines.Add(ReadStopLine(session!));
            if (acted) return;                        // the nested stop: record it and go on
            acted = true;

            // The user draws a breakpoint on double/2 (line 7) WHILE STOPPED. It goes down
            // the command channel and sits there unread — the engine is parked in this very
            // notify and will not drain until it resumes.
            session!.Channel.WriteCommands(
                new DebugCommand(DebugCommandKind.AddBreakpoint, "<string>", 7));

            // Now evaluate a goal that reaches it. Before the fix this ran to "R = 10" with
            // no stop; now it stops at the just-set breakpoint.
            evalResult = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
                Shumway.Core.Debugging.ShumwayDebugHost.EvaluateGoal(
                    0, Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes("double(5, R)")))));
        });
        using (session)
            engine.QueryAll("p(7).").ToList();

        Assert.Equal("R = 10", evalResult);           // the goal still answers
        Assert.Equal(2, stopLines.Count);             // the outer stop, then the evaluation's
        Assert.Equal(7, stopLines[1]);                // which stopped at the breakpoint just set
    }

    [Fact]
    public void MultipleSolutions_AreWalkedWithSemicolon_LikeTheRepl()
    {
        // member(X, [a,b,c]) has three solutions. The first EvaluateGoal gives the first; a bare
        // ";" gives each next; when they run out, "no more solutions".
        var engine = DebugEngine(Program);
        engine.AddBreakpoint("<string>", 4);

        var results = new List<string>();
        bool done = false;
        var svc = new DebugService(engine, (s, e) =>
        {
            if (done) return;
            done = true;
            results.Add(s.EvaluateGoal(0, "member(E, [a,b,c])"));   // E: free, not a frame var
            results.Add(s.EvaluateGoal(0, ";"));
            results.Add(s.EvaluateGoal(0, ";"));
            results.Add(s.EvaluateGoal(0, ";"));   // exhausted
        });
        engine.AttachDebugSession(svc);
        engine.QueryAll("p(7).").ToList();
        engine.AttachDebugSession(null);

        Assert.Equal(new[] { "E = a", "E = b", "E = c", "no more solutions" }, results);
    }

    [Fact]
    public void ASemicolonWithNothingParked_SaysSo()
    {
        var engine = DebugEngine(Program);
        engine.AddBreakpoint("<string>", 4);
        var (result, _) = EvalAtBreak(engine, "p(7).", ";");
        Assert.Contains("no evaluation to continue", result);
    }

    [Fact]
    public void ANewGoalAbandonsThePreviousBacktracking()
    {
        // Halfway through walking member/2, evaluating a different goal starts fresh; a ";"
        // afterwards continues the NEW goal, not the abandoned one.
        var engine = DebugEngine(Program);
        engine.AddBreakpoint("<string>", 4);

        var results = new List<string>();
        bool done = false;
        var svc = new DebugService(engine, (s, e) =>
        {
            if (done) return;
            done = true;
            results.Add(s.EvaluateGoal(0, "member(E, [a,b,c])"));   // E = a
            results.Add(s.EvaluateGoal(0, ";"));                    // E = b
            results.Add(s.EvaluateGoal(0, "member(F, [m,n])"));     // fresh: F = m
            results.Add(s.EvaluateGoal(0, ";"));                    // F = n
            results.Add(s.EvaluateGoal(0, ";"));                    // no more
        });
        engine.AttachDebugSession(svc);
        engine.QueryAll("p(7).").ToList();
        engine.AttachDebugSession(null);

        Assert.Equal(new[] { "E = a", "E = b", "F = m", "F = n", "no more solutions" }, results);
    }

    [Fact]
    public void SteppingAbandonsTheParkedEvaluation_AndTheStepIsCorrect()
    {
        // A goal parked mid-backtracking must not derail the next step: the suspended query's
        // depth and tables are restored before the step, so it lands on the next goal exactly
        // as TheSuspendedQueryResumesUndamaged shows for a single eval.
        var engine = DebugEngine(Program);
        engine.AddBreakpoint("<string>", 3);   // the call to q/2; r/1 is next

        var stops = new List<DebugStopEvent>();
        bool acted = false;
        var svc = new DebugService(engine, (s, e) =>
        {
            stops.Add(e);
            if (acted) return;
            acted = true;
            s.EvaluateGoal(0, "member(E, [a,b,c])");   // park mid-backtracking
            s.EvaluateGoal(0, ";");                     // one more, still parked
            s.Resume(StepMode.Over);                    // now step — abandons the eval
        });
        engine.AttachDebugSession(svc);
        var solutions = engine.QueryAll("p(7).").ToList();
        engine.AttachDebugSession(null);

        Assert.Single(solutions);
        Assert.Equal(2, stops.Count);
        Assert.Equal(StopReason.Call, stops[1].Reason);
        Assert.Equal("r/1", stops[1].Goal);
        Assert.StartsWith("?-", stops[1].Frames[^1].Name);
    }

    private static int ReadStopLine(ChannelDebugSession session)
    {
        var bytes = new byte[DebugChannel.SnapshotCapacity];
        Marshal.Copy(session.Channel.SnapshotAddress, bytes, 0, bytes.Length);
        return DebugChannel.ReadSnapshot(bytes)!.Line;
    }

    [Fact]
    public void TheChannelSnapshotIsPutBack_SoLocalsStillReadTheOriginalStop()
    {
        // The debugger's Locals read the snapshot buffer, and Visual Studio returns the
        // user to the ORIGINAL break state when the eval is done. The eval's stops
        // overwrite the buffer; the bracket restores it byte for byte.
        var engine = DebugEngine(Program);
        engine.AddBreakpoint("<string>", 4);

        ChannelDebugSession? session = null;
        var sequences = new List<int>();
        string evalResult = "";
        bool evaluated = false;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            var bytes = new byte[DebugChannel.SnapshotCapacity];
            Marshal.Copy(session!.Channel.SnapshotAddress, bytes, 0, bytes.Length);
            sequences.Add(DebugChannel.ReadSnapshot(bytes)!.Sequence);
            if (!evaluated)
            {
                evaluated = true;
                evalResult = Shumway.Core.Debugging.ShumwayDebugHost.EvaluateGoal(
                    0, Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes("p(1)")));

                // Back from the eval: the buffer holds the ORIGINAL stop again — same
                // frames, same goal, same sequence — as the Locals window will re-read it.
                Marshal.Copy(session.Channel.SnapshotAddress, bytes, 0, bytes.Length);
                var restored = DebugChannel.ReadSnapshot(bytes)!;
                Assert.Equal(sequences[0], restored.Sequence);
                Assert.Equal("(7)", restored.Frames[0].HeadArgs);
            }
        });
        using (session)
            engine.QueryAll("p(7).").ToList();

        // And the whole round-trip crossed as base64, the way the func-eval carries it.
        Assert.Equal("true", System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(evalResult)));
    }
}
