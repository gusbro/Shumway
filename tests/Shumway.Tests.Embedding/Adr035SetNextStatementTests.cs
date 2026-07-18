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
        // choice/1 leaves a CP; the cut kills it. Rewinding to line 4 (choice's port)
        // would need that CP back — refused, and the message lists what IS rewindable.
        //  2: runc(Out) :-
        //  3:     mark(M),
        //  4:     choice(X),
        //  5:     !,
        //  6:     use(X),
        //  7:     Out = done(M, X).
        //  8: mark(m).
        //  9: choice(1).
        // 10: choice(2).
        // 11: use(_).
        var engine = DebugEngine(
            "runc(Out) :-\n    mark(M),\n    choice(X),\n    !,\n    use(X),\n" +
            "    Out = done(M, X).\nmark(m).\nchoice(1).\nchoice(2).\nuse(_).\n");
        Assert.True(engine.AddBreakpoint("<string>", 6) > 0);

        var (results, _, sols) = Run(engine, "runc(Out).", 4);

        _log.WriteLine("refusal: " + results[0]);
        Assert.Contains("cannot rewind to line 4", results[0]);
        Assert.Single(sols);
        Assert.Equal("done(m,1)", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
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
}
