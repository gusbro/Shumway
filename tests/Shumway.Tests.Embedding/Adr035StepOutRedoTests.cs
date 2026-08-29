using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 — Step Out from a REDO port. The original report came
/// from a real program (its <c>concat/2</c>): F11 into a backtracking
/// predicate lands on its redo port, which reports the retried goal's CALL
/// depth (one shallower than its body); Step Out there must land on the goal
/// AFTER the predicate, not run out of the whole enclosing clause. The
/// synthetic program reproduces the shape self-contained: a multi-clause
/// predicate whose first clause fails after a head match (the redo), called
/// mid-conjunction inside a catch, with a builtin goal right after it.</summary>
[Collection("debugger")]
public class Adr035StepOutRedoTests
{
    private readonly ITestOutputHelper _log;
    public Adr035StepOutRedoTests(ITestOutputHelper log) => _log = log;

    private sealed class AbortWalk : Exception { }

    // Line numbers are load-bearing (breakpoints and landing assertions):
    //  1: main :-
    //  2:     catch(( first,
    //  3:             weld(hello, R),
    //  4:             current_prolog_flag(bounded, _),
    //  5:             last_goal(R)
    //  6:           ), _, true).
    //  7: first.
    //  8: last_goal(_).
    //  9: weld(A, R) :-
    // 10:     number(A),
    // 11:     R = num.
    // 12: weld(A, R) :-
    // 13:     atom(A),
    // 14:     R = at.
    private const string Source = """
        main :-
            catch(( first,
                    weld(hello, R),
                    current_prolog_flag(bounded, _),
                    last_goal(R)
                  ), _, true).
        first.
        last_goal(_).
        weld(A, R) :-
            number(A),
            R = num.
        weld(A, R) :-
            atom(A),
            R = at.
        """;

    private static string WriteSource()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_stepout_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(path, Source + "\n");
        return path;
    }

    private static PrologEngine ConsultSynthetic(string path)
    {
        var engine = new PrologEngine();
        engine.Flags.EmitDebugInfo = true;
        engine.Flags.DebugCodegen = true;
        engine.ConsultFile(path);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    /// <summary>Drive step decisions from a breakpoint; return every stop.
    /// The chooser sees each stop and answers with the next step (or null to
    /// abort the walk).</summary>
    private List<DebugStopEvent> Drive(
        PrologEngine engine, string path, int bpLine,
        Func<DebugStopEvent, int, StepMode?> chooser)
    {
        Assert.True(engine.AddBreakpoint(path, bpLine) > 0,
            $"breakpoint at line {bpLine} should bind");
        var stops = new List<DebugStopEvent>();
        var svc = new DebugService(engine, (s, e) =>
        {
            stops.Add(e);
            StepMode? next = chooser(e, stops.Count - 1);
            if (next is { } mode) s.Resume(mode);
            else throw new AbortWalk();
        });
        engine.AttachDebugSession(svc);
        try { engine.QueryAll("main.").ToList(); }
        catch (AbortWalk) { }
        catch (Exception ex) { _log.WriteLine("query ended " + ex.GetType().Name); }
        engine.AttachDebugSession(null);
        foreach (var s in stops)
            _log.WriteLine($"  {s.Reason,-11} {s.Goal,-24}@{s.Line} d{s.Depth}");
        return stops;
    }

    [Fact]
    public void StepOut_FromTheRedoPort_LandsOnTheGoalAfterThePredicate()
    {
        // F11 until weld/2's REDO port shows (number(hello) failed, clause 2
        // retries — the port reports weld's CALL depth), then Step Out.
        string path = WriteSource();
        try
        {
            var engine = ConsultSynthetic(path);
            bool steppedOut = false;
            var stops = Drive(engine, path, bpLine: 7, (e, i) =>
            {
                if (steppedOut) return null;              // capture the landing, stop
                if (e.Reason == StopReason.Redo && e.Goal == "weld/2")
                {
                    steppedOut = true;
                    return StepMode.Out;
                }
                return i < 20 ? StepMode.Into : null;     // bounded burrow
            });

            int redo = stops.FindIndex(
                s => s.Reason == StopReason.Redo && s.Goal == "weld/2");
            Assert.True(redo >= 0, "never reached weld/2's redo port");
            Assert.True(stops.Count > redo + 1,
                "Step Out ran off the end instead of stopping");
            var landed = stops[redo + 1];
            // The landing must be the goal AFTER weld in main's catch
            // conjunction — NOT StepAbandoned (ran to end) and NOT out at
            // main's own depth.
            Assert.NotEqual(StopReason.StepAbandoned, landed.Reason);
            Assert.Equal("current_prolog_flag/2", landed.Goal);
            Assert.Equal(4, landed.Line);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StepOut_FromInsideTheBody_LandsOnTheGoalAfterThePredicate()
    {
        // The already-working case, kept as a guard: a breakpoint genuinely
        // inside weld's second clause body Steps Out to the builtin goal
        // after it via the strict rule.
        foreach (int bp in new[] { 13, 14 })
        {
            string path = WriteSource();
            try
            {
                var engine = ConsultSynthetic(path);
                bool steppedOut = false;
                var stops = Drive(engine, path, bpLine: bp, (e, i) =>
                {
                    if (steppedOut) return null;
                    steppedOut = true;
                    return StepMode.Out;
                });
                Assert.True(stops.Count >= 2, $"bp {bp}: Step Out ran off the end");
                Assert.Equal(StopReason.Breakpoint, stops[0].Reason);
                Assert.Equal("current_prolog_flag/2", stops[1].Goal);
                Assert.Equal(4, stops[1].Line);
            }
            finally { File.Delete(path); }
        }
    }
}
