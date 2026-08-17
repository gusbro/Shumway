using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 D5 — conditional breakpoints. The breakpoint carries a Prolog goal;
/// when the <c>Break</c> is reached the goal runs in the frame it fired in (its variables
/// substituted by name, the Immediate-window recipe), ON THE ENGINE'S THREAD, before any
/// debugger hears of the hit: success stops, failure runs on, and a condition that cannot
/// run (syntax error, exception, timeout) stops WITH the error — a broken condition that
/// silently swallowed its breakpoint would be undiagnosable.</summary>
public class Adr035ConditionalBreakpointTests
{
    private readonly ITestOutputHelper _log;
    public Adr035ConditionalBreakpointTests(ITestOutputHelper log) => _log = log;

    //  1: :- set_prolog_flag(compile_mode, debug).   (prepended: all lines shift by one)
    //  2: run :-
    //  3:     between(1, 5, X),
    //  4:     use(X),
    //  5:     fail.
    //  6: run.
    //  7: use(_).
    private const string Program =
        "run :-\n" +
        "    between(1, 5, X),\n" +
        "    use(X),\n" +
        "    fail.\n" +
        "run.\n" +
        "use(_).\n";

    private static PrologEngine DebugEngine(string program = Program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    private List<DebugStopEvent> Run(PrologEngine engine, string goal = "run.")
    {
        var stops = new List<DebugStopEvent>();
        var svc = new DebugService(engine, (s, e) =>
        {
            stops.Add(e);
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll(goal).ToList();
        engine.AttachDebugSession(null);
        Assert.Single(sols);   // `run` always succeeds via its second clause
        foreach (var s in stops)
        {
            string x = s.Variables.FirstOrDefault(v => v.Name == "X").Value ?? "?";
            _log.WriteLine($"{s.Reason} {s.Goal} line={s.Line} X={x} condErr='{s.ConditionError}'");
        }
        return stops;
    }

    [Fact]
    public void ConditionOnAFrameVariable_StopsOnlyWhenItHolds()
    {
        var engine = DebugEngine();
        // use(X) runs for X = 1..5; the condition selects X > 3 → exactly two stops.
        Assert.True(engine.AddBreakpoint("<string>", 4, "X > 3") > 0);

        var stops = Run(engine);

        Assert.Equal(2, stops.Count);
        Assert.All(stops, s => Assert.Equal(StopReason.Breakpoint, s.Reason));
        Assert.All(stops, s => Assert.Equal("", s.ConditionError));
        // And the frame at each stop really is the one the condition selected.
        Assert.Equal(new[] { "4", "5" },
            stops.Select(s => s.Variables.First(v => v.Name == "X").Value).ToArray());
    }

    [Fact]
    public void ConditionThatNeverHolds_NeverStops()
    {
        var engine = DebugEngine();
        Assert.True(engine.AddBreakpoint("<string>", 4, "X > 100") > 0);

        var stops = Run(engine);

        Assert.Empty(stops);
    }

    [Fact]
    public void ConditionSetUnderAnotherSpellingOfTheFile_StillGoverns()
    {
        // The engine consulted the FULL path; the debugger names the file by its
        // base name. One file, one id, two spellings — and the condition must
        // follow the breakpoint across them. It was once keyed by the spelling
        // the debugger used, which the hit never reports: the breakpoint stopped
        // as if unconditional, silently.
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "shumway_condspell");
        System.IO.Directory.CreateDirectory(dir);
        string file = System.IO.Path.Combine(dir, "condspell.pl");
        System.IO.File.WriteAllText(file, Program);   // use(X) is on line 3 here

        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).");
        engine.ReconsultFile(file);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        Assert.True(engine.AddBreakpoint("condspell.pl", 3, "X > 100") > 0);

        var stops = Run(engine);

        Assert.Empty(stops);   // the condition governs: it never holds
    }

    [Fact]
    public void ConditionCanCallAUserPredicate()
    {
        // The condition is a full Prolog goal, not just arithmetic: it can call the
        // program's own predicates against the frame's bindings.
        var engine = DebugEngine(Program + "interesting(2).\ninteresting(4).\n");
        Assert.True(engine.AddBreakpoint("<string>", 4, "interesting(X)") > 0);

        var stops = Run(engine);

        Assert.Equal(2, stops.Count);
        Assert.Equal(new[] { "2", "4" },
            stops.Select(s => s.Variables.First(v => v.Name == "X").Value).ToArray());
    }

    [Fact]
    public void ConditionWithASyntaxError_StopsAndSaysWhy()
    {
        var engine = DebugEngine();
        Assert.True(engine.AddBreakpoint("<string>", 4, "X > ) 3") > 0);

        var stops = Run(engine);

        // Stops at every hit (5), each carrying the error — never silently swallowed.
        Assert.Equal(5, stops.Count);
        Assert.All(stops, s => Assert.Contains("syntax error", s.ConditionError));
    }

    [Fact]
    public void ConditionThatThrows_StopsAndSaysWhy()
    {
        var engine = DebugEngine();
        // Y is unbound in the frame → is/2 raises instantiation_error.
        Assert.True(engine.AddBreakpoint("<string>", 4, "Z is Y + 1, Z > 0") > 0);

        var stops = Run(engine);

        Assert.Equal(5, stops.Count);
        Assert.All(stops, s => Assert.Contains("condition error", s.ConditionError));
    }

    [Fact]
    public void RemovingTheBreakpoint_DropsItsCondition()
    {
        var engine = DebugEngine();
        Assert.True(engine.AddBreakpoint("<string>", 4, "X > 3") > 0);
        engine.RemoveBreakpoint("<string>", 4);
        // Re-armed WITHOUT a condition: it must stop on every hit, not remember "X > 3".
        Assert.True(engine.AddBreakpoint("<string>", 4) > 0);

        var stops = Run(engine);

        Assert.Equal(5, stops.Count);
    }

    [Fact]
    public void ReAddingWithoutACondition_MakesItUnconditional()
    {
        var engine = DebugEngine();
        Assert.True(engine.AddBreakpoint("<string>", 4, "X > 3") > 0);
        // The debugger writes its whole desired state each time: an add for the same
        // breakpoint without a condition means the user cleared it.
        Assert.True(engine.AddBreakpoint("<string>", 4) > 0);

        var stops = Run(engine);

        Assert.Equal(5, stops.Count);
    }

    [Fact]
    public void ConditionSurvivesAcrossQueries()
    {
        var engine = DebugEngine();
        Assert.True(engine.AddBreakpoint("<string>", 4, "X > 3") > 0);

        var first = Run(engine);
        var second = Run(engine);   // the rebind path must keep the condition

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void ConditionWithABreakpointInItsPath_DoesNotRecurse()
    {
        // The condition calls a predicate that ITSELF carries a breakpoint. A stop inside
        // a condition would recurse into evaluating the condition again; it must be skipped.
        //  8: check(X) :-
        //  9:     X > 3.
        var engine = DebugEngine(Program + "check(X) :-\n    X > 3.\n");
        Assert.True(engine.AddBreakpoint("<string>", 4, "check(X)") > 0);
        Assert.True(engine.AddBreakpoint("<string>", 9) > 0);   // inside check/1

        var stops = Run(engine);

        // check/1 is only ever reached by the condition here, so its breakpoint reports
        // nothing; the conditional breakpoint stops exactly where X > 3.
        Assert.Equal(2, stops.Count);
        Assert.All(stops, s => Assert.Equal(4, s.BreakLine));
    }

    [Fact]
    public void ConditionEvaluation_UnderHeavyAssertz_DoesNotDerailTheOuterQuery()
    {
        // The Blint crash. A condition is evaluated at EVERY hit, and each evaluation is a
        // nested query whose setup used to run the chunk-158 auto-compaction when the
        // accumulated assertz count crossed the watermark — "the safe point: no in-flight
        // choice points hold addresses into it", said the comment. False for a NESTED
        // debug evaluation: the OUTER query is in flight, its Break bytes live in the old
        // buffer, and the compaction re-linked the breakpoint table against the new one —
        // so the outer query's next Break dispatched against a table that no longer
        // described it, and the whole program died of "code space out of step".
        //
        //  2: :- dynamic(d/1).
        //  3: run :-
        //  4:     between(1, N, X),
        //  5:     assertz(d(X)),
        //  6:     use(X),
        //  7:     fail.
        //  8: run.
        //  9: use(_).
        var engine = DebugEngine("""
            :- dynamic(d/1).
            run :-
                between(1, 1200, X),
                assertz(d(X)),
                use(X),
                fail.
            run.
            use(_).
            """);
        // 1200 mutations cross the default watermark (1000) mid-query, guaranteed
        // (the per-port condition eval makes each iteration expensive — 1200 keeps
        // the crossing + margin at ~half the wall time of the original 2000).
        Assert.True(engine.AddBreakpoint("<string>", 6, "X > 1195") > 0);

        var stops = Run(engine);   // asserts the query still yields its solution

        Assert.Equal(5, stops.Count);   // X = 1196..1200 — and nothing crashed
        Assert.All(stops, s => Assert.Equal("", s.ConditionError));
    }

    [Fact]
    public void ConditionEvaluation_WithUndeclaredDynamicAsserts_DoesNotDerailTheOuterQuery()
    {
        // The other half of the Blint shape: the asserted predicate is UNDECLARED, so its
        // implicit_dynamic auto-promotion mid-query takes the NON-OWNER invalidation path
        // (the persistent buffer is nulled while the outer query flies). Every condition
        // evaluation's nested setup then finds nothing to reuse and must rebuild — and the
        // rebuilt buffer must not steal the breakpoint table from the outer query's.
        //
        //  2: run :-
        //  3:     between(1, 40, X),
        //  4:     assertz(und(X)),
        //  5:     use(X),
        //  6:     fail.
        //  7: run.
        //  8: use(_).
        var engine = DebugEngine("""
            run :-
                between(1, 40, X),
                assertz(und(X)),
                use(X),
                fail.
            run.
            use(_).
            """);
        Assert.True(engine.AddBreakpoint("<string>", 5, "X > 35") > 0);

        var stops = Run(engine);

        Assert.Equal(5, stops.Count);   // X = 36..40
        Assert.All(stops, s => Assert.Equal("", s.ConditionError));

        // And the asserted facts are all there — the outer query ran to completion sound.
        var xs = engine.Query<List<int>>("findall(X, und(X), Xs).", "Xs").Single();
        Assert.Equal(40, xs.Count);
    }

    [Fact]
    public void AnErroringCondition_NeverLeaksIntoTheProgramsOwnCatch()
    {
        // The Blint crash's other face. The condition machinery runs INSIDE the outer
        // query's dispatch loop: an exception that escaped it would land in the outer
        // RunCatching — where the PROGRAM's own catch/3 would eat it, sending the program
        // down an error path it never takes without a debugger (or, uncaught, killing the
        // query with the RunCatching→Query→Main stack the user saw). The condition's error
        // must surface ONLY as ConditionError on the stop.
        //
        //  2: main(R) :-
        //  3:     catch(work, E, recover(E, R)).
        //  4: work :-
        //  5:     between(1, 5, X),
        //  6:     use(X),
        //  7:     fail.
        //  8: work.
        //  9: use(_).
        // 10: recover(E, caught(E)).
        var engine = DebugEngine("""
            main(R) :-
                catch(work, E, recover(E, R)).
            work :-
                between(1, 5, X),
                use(X),
                fail.
            work.
            use(_).
            recover(E, caught(E)).
            """);
        // Y is unbound in the frame → is/2 raises → the condition ERRORS at every hit.
        Assert.True(engine.AddBreakpoint("<string>", 6, "Z is Y + 1, Z > 0") > 0);

        var stops = new List<DebugStopEvent>();
        var svc = new DebugService(engine, (s, e) => { stops.Add(e); s.Resume(StepMode.Continue); });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll("main(R).").ToList();
        engine.AttachDebugSession(null);

        // The program's catch/3 never fired: main succeeded through work's second clause,
        // leaving R unbound — NOT bound to caught(...).
        Assert.Single(sols);
        Assert.DoesNotContain("caught", sols[0]["R"]?.ToString() ?? "");

        // The error surfaced where it belongs: on every stop, as ConditionError.
        Assert.Equal(5, stops.Count);
        Assert.All(stops, s => Assert.Contains("condition error", s.ConditionError));
    }

    [Fact]
    public void ConditionSideEffectsPersist_LikeAnImmediateWindowGoal()
    {
        // Conditions run in the live engine (the C# debugger contract: a condition CAN
        // have side effects; they are expected to be tests, but the engine does not lie
        // about what ran). An assertz made by the condition is visible afterwards.
        var engine = DebugEngine(":- dynamic(seen/1).\n" + Program);
        // Program shifted one MORE line by the :- dynamic directive: use(X) is line 5.
        Assert.True(engine.AddBreakpoint("<string>", 5, "assertz(seen(X)), X > 100") > 0);

        var stops = Run(engine);

        Assert.Empty(stops);   // X > 100 never holds
        var seen = engine.Query<List<int>>("findall(X, seen(X), Xs).", "Xs").Single();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, seen);
    }
}
