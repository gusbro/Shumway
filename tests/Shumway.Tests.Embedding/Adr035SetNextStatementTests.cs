using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 D5+ — Set Next Statement.
///
/// <para>FORWARD moves the pointer; the skipped goals never run (C# semantics).
/// BACKWARD rewinds to the recorded mark of an earlier goal's call port: choice points
/// created since are discarded, both trails unwind to the recorded tops — and because a
/// debug session turns <see cref="Shumway.Core.Activation.TrailEverything"/> on, that
/// undoes EVERY binding made since, including the ones the HB optimisation would have
/// left untrailed. Nothing re-executes; the user continues from there themselves. The
/// HEAD span rewinds to the caller's mark for the call, so continuing re-runs the call —
/// head unification is pure, so that replay is safe.</para></summary>
public class Adr035SetNextStatementTests
{
    private readonly ITestOutputHelper _log;
    public Adr035SetNextStatementTests(ITestOutputHelper log) => _log = log;

    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    // The counter makes re-execution OBSERVABLE: each pass through one/1 yields a new
    // value. A rewind that failed to unbind A would leave the old value bound, and the
    // re-run's fresh value would fail to unify — so `t(2,...)` in the answer proves both
    // the unbind and the (user-driven) re-run.
    //  2: :- dynamic(c/1).
    //  3: c(0).
    //  4: run(Out) :-
    //  5:     one(A),
    //  6:     two(B),
    //  7:     three(C),
    //  8:     Out = t(A, B, C).
    //  9: one(A) :- retract(c(A0)), A is A0 + 1, assertz(c(A)).
    // 10: two(20).
    // 11: three(30).
    private const string Program =
        ":- dynamic(c/1).\n" +
        "c(0).\n" +
        "run(Out) :-\n" +
        "    one(A),\n" +
        "    two(B),\n" +
        "    three(C),\n" +
        "    Out = t(A, B, C).\n" +
        "one(A) :- retract(c(A0)), A is A0 + 1, assertz(c(A)).\n" +
        "two(20).\n" +
        "three(30).\n";

    /// <summary>Stops at the breakpoint; at stop N runs actions[N] (a SetNextStatement
    /// line or -1 for plain continue); returns (answers, stopCount, solutions).</summary>
    private (List<string> Results, int Stops, List<Solution> Sols) Run(
        PrologEngine engine, string goal, params int[] snsAtStop)
    {
        var results = new List<string>();
        int stop = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            int idx = stop++;
            if (idx < snsAtStop.Length && snsAtStop[idx] >= 0)
            {
                string r = s.SetNextStatement(0, snsAtStop[idx]);
                results.Add(r);
                _log.WriteLine($"stop {idx}: SNS -> line {snsAtStop[idx]} => '{r}'");
            }
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll(goal).ToList();
        engine.AttachDebugSession(null);
        return (results, stop, sols);
    }

    [Fact]
    public void Forward_SkipsTheGoalsInBetween()
    {
        // Stop at line 5 (one/1's call port), jump to line 7: one and two never run —
        // A and B stay free, the counter stays at 0, three(C) runs.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 5) > 0);

        var (results, stops, sols) = Run(engine, "run(Out).", 7);

        Assert.Equal("", results[0]);
        Assert.Equal(1, stops);
        Assert.Single(sols);
        string outText = sols[0]["Out"]!.ToString()!.Replace(" ", "");
        Assert.Matches(@"^t\(_\w+,_\w+,30\)$", outText);
        // one/1 never ran: the counter is untouched.
        Assert.Equal(0, engine.Query<int>("c(N).", "N").Single());
    }

    [Fact]
    public void AForwardMoveIsPure_SoMovingBackAgainIsAccepted()
    {
        // The user's report (prueba.pl): breakpoint at the first goal, Set Next Statement
        // forward to the clause's last goal — then, BEFORE running anything, back to the
        // first goal. Refused: "the only valid target is the line you are on". But a
        // forward move executes nothing, so the machine state at every skipped site IS
        // the current state — the move records a mark apiece, and the return trip (and
        // any intermediate hop) stays valid. Both moves answer ""; the counter proves
        // one/1 then ran exactly once on the real pass.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 5) > 0);

        // Stop once at line 5: jump to 8 (skipping one, two, three), then back to 5.
        var results = new List<string>();
        int stops = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            if (stops++ == 0)
            {
                results.Add(s.SetNextStatement(0, 8));
                results.Add(s.SetNextStatement(0, 5));
            }
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll("run(Out).").ToList();
        engine.AttachDebugSession(null);

        Assert.Equal(new[] { "", "" }, results);
        Assert.Equal(1, stops);
        Assert.Single(sols);
        // Back at line 5, the continue ran the whole body: t(1,20,30).
        Assert.Equal("t(1,20,30)", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
    }

    [Fact]
    public void TheFirstStepAfterAMove_ExecutesTheMovedToGoal()
    {
        // The user's report: after a Set Next Statement, the first F10/F11 did nothing —
        // the resume stopped at the moved-to goal's own call port, exactly where the
        // arrow already stood — and only the second step ran the goal. The first stop
        // decision AT the moved-to site is suppressed: a step taken from the move
        // EXECUTES the goal under the arrow and stops at the NEXT one.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 5) > 0);

        var stopLines = new List<int>();
        int stops = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            stopLines.Add(e.Line);
            // Stop 0 (bp at 5): move to 6 (two's call), then STEP: two/1 must execute and
            // the step land on 7 (three's call) — not "stop" at 6 again.
            if (stops++ == 0)
            {
                Assert.Equal("", s.SetNextStatement(0, 6));
                s.Resume(StepMode.Into);
            }
            else
            {
                s.Resume(StepMode.Continue);
            }
        });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll("run(Out).").ToList();
        engine.AttachDebugSession(null);

        _log.WriteLine("stop lines: " + string.Join(", ", stopLines));
        Assert.Equal(2, stopLines.Count);
        Assert.Equal(5, stopLines[0]);   // the breakpoint
        Assert.Equal(7, stopLines[1]);   // the step EXECUTED two/1 and landed on three
        Assert.Single(sols);
        // one/1 never ran (skipped by the move): A free, B bound by the executed two/1.
        Assert.Matches(@"^t\(_\w+,20,30\)$", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
    }

    [Fact]
    public void Backward_RewindsBindings_AndTheUserRerunsTheGoals()
    {
        // Stop at line 7 (three's call port; one and two already ran, A=1, B=20).
        // Rewind to line 5: A and B must be UNBOUND again — then continuing re-runs
        // one/1 (counter: 1 -> 2) and two/2, and the breakpoint at 7 hits a second time.
        // Final answer t(2, 20, 30): the 2 proves the rewind really unbound A (a stale
        // A=1 would have failed against the re-run's A is 1+1).
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 7) > 0);

        var (results, stops, sols) = Run(engine, "run(Out).", 5, -1);

        Assert.Equal("", results[0]);
        Assert.Equal(2, stops);                       // the rewound path hits 7 again
        Assert.Single(sols);
        Assert.Equal("t(2,20,30)", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
        Assert.Equal(2, engine.Query<int>("c(N).", "N").Single());   // one/1 ran twice
    }

    [Fact]
    public void Backward_ToAGoalThatNeverRan_IsRefusedWithTheAcceptableLines()
    {
        // Stop at line 5 on the FIRST stop: lines 6/7 have not run — no marks. A rewind
        // request to line 6 is nonsense (it is FORWARD from here, accepted as a jump);
        // ask instead for a backward target in another clause: line 9 is not a statement
        // of THIS clause at all.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 5) > 0);

        var (results, _, sols) = Run(engine, "run(Out).", 9);

        Assert.Contains("not a statement of this clause", results[0]);
        Assert.Contains("5", results[0]);   // the message names the clause's lines
        Assert.Single(sols);
    }

    [Fact]
    public void Backward_PastACut_IsRefused()
    {
        // The genuinely irrecoverable rewind: use(X)'s mark was recorded WHILE choice's
        // choice point was alive — its saved B IS that CP — and the ! then discarded it.
        // No trail can bring a dead choice point back, so line 4 is refused, and the
        // message lists what IS rewindable: line 3 (choice's own port, whose saved B
        // predates the CP — rewinding THERE is fine, the re-run recreates the CP).
        //  2: runc(Out) :-
        //  3:     choice(X),
        //  4:     use(X),
        //  5:     !,
        //  6:     Out = done(X).
        //  7: choice(1).
        //  8: choice(2).
        //  9: use(_).
        var engine = DebugEngine(
            "runc(Out) :-\n    choice(X),\n    use(X),\n    !,\n" +
            "    Out = done(X).\nchoice(1).\nchoice(2).\nuse(_).\n");
        Assert.True(engine.AddBreakpoint("<string>", 6) > 0);

        var (results, _, sols) = Run(engine, "runc(Out).", 4);

        _log.WriteLine("refusal: " + results[0]);
        Assert.Contains("cannot rewind to line 4", results[0]);
        Assert.Contains("3", results[0]);   // what IS rewindable
        Assert.Single(sols);
        Assert.Equal("done(1)", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
    }

    [Fact]
    public void BackToHead_RestartsTheClauseBody()
    {
        // Stop at line 7; Set Next Statement to line 4 (the clause HEAD): rewinds to the
        // FIRST body goal — head-unification bindings survive (they predate the first
        // goal's mark), the body restarts, exactly what C#'s Set Next Statement to a
        // method's first line means (parameters keep their values). one/1 re-runs on the
        // user's continue (counter 2), the breakpoint hits again on the second pass.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 7) > 0);

        var (results, stops, sols) = Run(engine, "run(Out).", 4, -1);

        Assert.Equal("", results[0]);
        Assert.Equal(2, stops);
        Assert.Single(sols);
        Assert.Equal("t(2,20,30)", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
    }

    [Fact]
    public void Backward_WorksForBindingsTheHbCheckWouldHaveSkipped()
    {
        // The reason TrailEverything exists. Deterministic facts create NO choice points,
        // so without the debug session's trail-everything the bindings of one/1 and two/2
        // would be untrailed and unrecoverable — the rewind would leave A=1 bound and the
        // re-run would FAIL. This test is the same as Backward_Rewinds... but stated as
        // the property: the query SUCCEEDS after a rewind across CP-free bindings.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 8) > 0);   // Out = t(...) port

        var (results, stops, sols) = Run(engine, "run(Out).", 6, -1);

        Assert.Equal("", results[0]);
        Assert.Equal(2, stops);
        Assert.Single(sols);
        // two and three re-ran (pure); one did NOT re-run (rewind to 6, not 5): counter 1.
        Assert.Equal("t(1,20,30)", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
        Assert.Equal(1, engine.Query<int>("c(N).", "N").Single());
    }

    [Fact]
    public void MarksSurviveHeavyAllocation_TheGcRelocatesThemThroughCompaction()
    {
        // The Blint report: after F10-stepping a few goals of a real program, backward
        // targets had almost all vanished — a watermark heap GC had run mid-step and the
        // marks were dropped wholesale. The collection now RELOCATES them instead
        // (IDebugSession.RelocateHeapRoots): the order-preserving slide maps each mark's
        // saved allocation point through the forwarding count, trailed cells are roots so
        // the saved trail tops stay true as recorded, and GcCount is refreshed. Here
        // big/1 allocates far past the default watermark — the GC runs mid-goal — and
        // the rewind across it still answers "" and re-executes correctly.
        //  2: run(Out) :- first(A), big(B), Out = done(A, B).
        //  3: first(1).
        //  4: big(N) :- numlist(1, 400000, L), length(L, N).
        var engine = DebugEngine(
            "run(Out) :-\n    first(A),\n    big(B),\n    Out = done(A, B).\n" +
            "first(1).\nbig(N) :- numlist(1, 400000, L), length(L, N).\n");
        Assert.True(engine.AddBreakpoint("<string>", 5) > 0);   // Out = done(A, B)

        var (results, stops, sols) = Run(engine, "run(Out).", 3, -1);

        // Rewinds to first(A)'s line — recorded BEFORE the huge allocation. Continue
        // re-runs first and big: the query still answers.
        Assert.Equal("", results[0]);
        Assert.Equal(2, stops);
        Assert.Single(sols);
        Assert.Equal("done(1,400000)", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
    }

    [Fact]
    public void MarksSurviveCuts_TrailCompactionStandsDownUnderDebug()
    {
        // The Blint report, round two: with the GC relocation in place, backward targets
        // STILL vanished after stepping a few goals. The killer was cut-time trail
        // compaction (Warren's optimisation, run at every !): it drops entries no future
        // backtrack could need — but under TrailEverything those entries are the
        // DEBUGGER'S HISTORY, and a real program cuts in every clause. The trail
        // collapsed to a handful of entries and the marks' saved tops read as
        // "backtracked past" and were purged. Compaction now stands down under a debug
        // session, like the pinned Hb. Every callee here cuts; the rewind across them
        // must survive.
        //  2: :- dynamic(c/1).  3: c(0).
        //  4: run(Out) :- one(A), two(B), three(C), Out = t(A, B, C).
        //  5..: one/two/three, each ending in !.
        var engine = DebugEngine(
            ":- dynamic(c/1).\nc(0).\n" +
            "run(Out) :-\n    one(A),\n    two(B),\n    three(C),\n    Out = t(A, B, C).\n" +
            "one(A) :- retract(c(A0)), A is A0 + 1, assertz(c(A)), !.\n" +
            "two(20) :- !.\nthree(30) :- !.\n");
        Assert.True(engine.AddBreakpoint("<string>", 8) > 0);   // Out = t(...)

        var (results, stops, sols) = Run(engine, "run(Out).", 5, -1);

        // Back to one(A)'s line across three cut-committing callees: accepted, and the
        // re-run bumps the counter to 2 — proving the rewind was real.
        Assert.Equal("", results[0]);
        Assert.Equal(2, stops);
        Assert.Single(sols);
        Assert.Equal("t(2,20,30)", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
    }

    // ---- cross-frame (ADR-035 D5+ generalization): SNS on a LOWER frame of the stack ----

    // A caller with observable per-goal effects, and a callee deep enough to stop inside:
    //  2: :- dynamic(log/1).
    //  3: caller(Out) :-
    //  4:     tag(a),
    //  5:     middle(V),
    //  6:     tag(b),
    //  7:     Out = done(V).
    //  8: middle(V) :-
    //  9:     tag(m),
    // 10:     V = inner.
    // 11: tag(T) :- assertz(log(T)).
    private const string NestedProgram =
        ":- dynamic(log/1).\n" +
        "caller(Out) :-\n" +
        "    tag(a),\n" +
        "    middle(V),\n" +
        "    tag(b),\n" +
        "    Out = done(V).\n" +
        "middle(V) :-\n" +
        "    tag(m),\n" +
        "    V = inner.\n" +
        "tag(T) :- assertz(log(T)).\n";

    [Fact]
    public void CrossFrame_BackwardOnTheCallerFrame_PopsTheCalleeAndRewinds()
    {
        // Stopped INSIDE middle/1 (line 10, V = inner — tag(m) has run). SNS on FRAME 1
        // (caller/1) back to line 4 (tag(a)): the callee frame pops, the trail unwinds to
        // tag(a)'s mark — V free again — and the continue re-runs the body from tag(a).
        // The log shows the re-execution: a, m, a, m, b (tag(a) and middle both ran
        // twice; the bp is one-shot per pass so the second pass stops again — continue).
        var engine = DebugEngine(NestedProgram);
        Assert.True(engine.AddBreakpoint("<string>", 10) > 0);

        var results = new List<string>();
        int stops = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            if (stops++ == 0)
                results.Add(s.SetNextStatement(1, 4));
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll("caller(Out).").ToList();
        engine.AttachDebugSession(null);

        Assert.Equal(new[] { "" }, results);
        Assert.Equal(2, stops);   // the second pass hits the bp in middle again
        Assert.Single(sols);
        Assert.Equal("done(inner)", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
        // First pass: a, m (stopped mid-middle, rewound). Second pass: a, m, b.
        Assert.Equal(new[] { "a", "m", "a", "m", "b" },
            engine.Query<string>("findall(T, log(T), L), atomic_list_concat(L, ',', A).", "A")
                .Single().Split(','));
    }

    [Fact]
    public void CrossFrame_ForwardOnTheCallerFrame_AbandonsTheCalleeAndSkips()
    {
        // Stopped inside middle/1. SNS on FRAME 1 FORWARD to line 7 (Out = done(V)):
        // rewinds to the caller's current goal (the middle call — undoing tag(m)'s
        // assert? no: asserts are permanent, but V's binding unwinds), then moves PAST
        // tag(b) without running it. V stays free (middle never completed), b never logs.
        var engine = DebugEngine(NestedProgram);
        Assert.True(engine.AddBreakpoint("<string>", 10) > 0);

        var results = new List<string>();
        int stops = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            if (stops++ == 0)
                results.Add(s.SetNextStatement(1, 7));
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll("caller(Out).").ToList();
        engine.AttachDebugSession(null);

        Assert.Equal(new[] { "" }, results);
        Assert.Equal(1, stops);
        Assert.Single(sols);
        // V free (middle abandoned), tag(b) skipped: Out = done(_) and the log is a, m.
        Assert.Matches(@"^done\(_\w+\)$", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
        Assert.Equal(new[] { "a", "m" },
            engine.Query<string>("findall(T, log(T), L), atomic_list_concat(L, ',', A).", "A")
                .Single().Split(','));
    }

    [Fact]
    public void CrossFrame_TheSameMoveAppliedTwice_RunsOnce()
    {
        // The eager Locals-refresh apply runs the move, then the resume drain re-applies
        // the same queued command. Cross-frame that is NOT naturally idempotent: the
        // first apply pops frames and the indices shift, so "frame 1" would resolve
        // against a different frame — under recursion, one whose clause accepts the same
        // line, rewinding twice. The per-stop guard makes the second apply a no-op.
        var engine = DebugEngine(NestedProgram);
        Assert.True(engine.AddBreakpoint("<string>", 10) > 0);

        var results = new List<string>();
        int stops = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            if (stops++ == 0)
            {
                results.Add(s.SetNextStatement(1, 4));   // the eager apply
                results.Add(s.SetNextStatement(1, 4));   // the drain's re-apply
            }
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll("caller(Out).").ToList();
        engine.AttachDebugSession(null);

        Assert.Equal(new[] { "", "" }, results);   // both answer "", ONE move happened
        Assert.Single(sols);
        Assert.Equal("done(inner)", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
        // One rewind, one re-run: a, m, a, m, b — a double rewind would have logged more.
        Assert.Equal(new[] { "a", "m", "a", "m", "b" },
            engine.Query<string>("findall(T, log(T), L), atomic_list_concat(L, ',', A).", "A")
                .Single().Split(','));
    }

    [Fact]
    public void CrossFrame_TheCallerFramePublishesItsOwnValidLines()
    {
        // Stopped inside middle/1: frame 0 (middle) and frame 1 (caller) each carry their
        // own Set Next Statement targets in the stop's frames. middle at line 10: back to
        // 9 (its mark), current 10 — no forward (10 is its last... V = inner is followed
        // by nothing) ; caller at line 5: 4 (tag(a)'s mark), 5 (its current goal), and
        // forward 6, 7 via the rewind-to-current anchor. The head lines ride along.
        var engine = DebugEngine(NestedProgram);
        Assert.True(engine.AddBreakpoint("<string>", 10) > 0);

        IReadOnlyList<int>? frame0Lines = null, frame1Lines = null;
        var svc = new DebugService(engine, (s, e) =>
        {
            if (frame0Lines is null && e.Frames.Count >= 2)
            {
                frame0Lines = e.Frames[0].SetNextLines;
                frame1Lines = e.Frames[1].SetNextLines;
            }
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        engine.QueryAll("caller(Out).").ToList();
        engine.AttachDebugSession(null);

        Assert.NotNull(frame0Lines);
        _log.WriteLine("frame0 (middle): " + string.Join(", ", frame0Lines!));
        _log.WriteLine("frame1 (caller): " + string.Join(", ", frame1Lines!));
        Assert.Contains(9, frame0Lines!);    // middle: backward to tag(m)
        Assert.Contains(10, frame0Lines!);   // middle: its current line
        Assert.Contains(4, frame1Lines!);    // caller: backward to tag(a)
        Assert.Contains(5, frame1Lines!);    // caller: its current goal (pop the callee)
        Assert.Contains(6, frame1Lines!);    // caller: forward past the call
        Assert.Contains(7, frame1Lines!);    // caller: forward to the last goal
    }
}
