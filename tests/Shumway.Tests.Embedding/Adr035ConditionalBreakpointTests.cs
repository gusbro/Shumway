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
