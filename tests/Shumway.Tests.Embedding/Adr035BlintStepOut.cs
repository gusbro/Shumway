using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 — Step Out from a REDO port. Reproduces the user's Blint report: F11 into a
/// backtracking predicate (concat/2) lands on its redo port, which reports the retried goal's
/// CALL depth (one shallower than its body); Step Out there must land on the goal AFTER the
/// predicate, not run out of the whole enclosing clause.</summary>
public class Adr035BlintStepOut
{
    private readonly ITestOutputHelper _log;
    public Adr035BlintStepOut(ITestOutputHelper log) => _log = log;

    private sealed class AbortWalk : Exception { }

    private static PrologEngine ConsultBlint(string blint)
    {
        var engine = new PrologEngine();
        engine.Flags.EmitDebugInfo = true;
        engine.Flags.DebugCodegen = true;
        // Blint is an ARITY-era program (`Char = '/'`-style quoted operator-atom
        // operands throughout its tokenizer): give it the quoted-operand
        // leniency alone — full arity_compat would change its LEXING too
        // (Arity $...$ strings, backslash not an escape), which this file
        // never consulted under.
        engine.Flags.LenientBareOperatorOperands = true;
        engine.ConsultFile(blint);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    /// <summary>Drive a fixed step script from a breakpoint; return every stop.</summary>
    private List<DebugStopEvent> Drive(PrologEngine engine, IReadOnlyList<StepMode> afterHit)
    {
        var stops = new List<DebugStopEvent>();
        int idx = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            stops.Add(e);
            if (idx < afterHit.Count) s.Resume(afterHit[idx++]);
            else throw new AbortWalk();   // stop before main runs on toward halt(9)
        });
        engine.AttachDebugSession(svc);
        try { engine.QueryAll("main.").ToList(); }
        catch (AbortWalk) { }
        catch (Exception ex) { _log.WriteLine("query ended " + ex.GetType().Name); }
        engine.AttachDebugSession(null);
        return stops;
    }

    [Fact]
    public void StepOut_FromConcatRedoPort_LandsOnTheGoalAfterConcat()
    {
        const string blint = @"C:\temp\Blint.pl";
        if (!File.Exists(blint)) return;   // only runs on the dev box that has Blint

        // F11 walk from main's first goal reaches (F11[5]) the redo port of concat/2 at line
        // 2494, depth 4 (concat's call depth — number('BLint v') in clause 2 failed, retrying
        // clause 4). That is where the user is "inside concat". Step Out from there.
        var engine = ConsultBlint(blint);
        engine.AddBreakpoint(blint, 20);   // writeln(starting_blint), the first breakpoint

        var script = new List<StepMode>();
        for (int i = 0; i < 5; i++) script.Add(StepMode.Into);   // burrow to the redo port
        script.Add(StepMode.Out);                                // and step out of concat

        var stops = Drive(engine, script);
        foreach (var s in stops)
            _log.WriteLine($"  {s.Reason,-11} {s.Goal,-24}@{s.Line} d{s.Depth}");

        // stops[5] is the redo port we stepped out FROM; stops[6] is where Step Out landed.
        Assert.True(stops.Count >= 7, "Step Out ran off the end instead of stopping");
        var from = stops[5];
        var landed = stops[6];
        Assert.Equal(StopReason.Redo, from.Reason);
        Assert.Equal("concat/2", from.Goal);

        // The landing must be a real goal in main's catch conjunction — NOT StepAbandoned
        // (ran to end) and NOT out at main's own depth.
        Assert.NotEqual(StopReason.StepAbandoned, landed.Reason);
        Assert.Equal("current_prolog_flag/2", landed.Goal);
        Assert.Equal(24, landed.Line);
    }

    [Fact]
    public void StepOut_FromInsideConcatBody_LandsOnTheGoalAfterConcat()
    {
        const string blint = @"C:\temp\Blint.pl";
        if (!File.Exists(blint)) return;

        // The already-working case, kept as a guard: a breakpoint genuinely inside concat's
        // body (call depth 5) Steps Out to current_prolog_flag (depth 4) via the strict rule.
        foreach (int bp in new[] { 2496, 2497 })
        {
            var engine = ConsultBlint(blint);
            engine.AddBreakpoint(blint, bp);

            var stops = Drive(engine, new[] { StepMode.Out });
            Assert.True(stops.Count >= 2, $"bp {bp}: Step Out ran off the end");
            Assert.Equal(StopReason.Breakpoint, stops[0].Reason);
            Assert.Equal("concat/2", stops[0].Goal);
            Assert.Equal("current_prolog_flag/2", stops[1].Goal);
            Assert.Equal(24, stops[1].Line);
        }
    }
}
