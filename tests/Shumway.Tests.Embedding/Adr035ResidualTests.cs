using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 — residual constraints of attributed variables at a stop.
///
/// <para>An attributed variable's Locals value is a bare <c>_G</c> name; what the user
/// wants to see is the projection the REPL prints for an answer — <c>X in 6..9</c>. The
/// debug service transplants the suspended activation's attributed variables into a
/// nested evaluation and runs the standard <c>attribute_goals</c> projection there, so a
/// stop's frames carry per-variable residual rows, and an Immediate-window goal sees the
/// frame variables WITH their constraints.</para>
/// </summary>
public class Adr035ResidualTests
{
    private readonly ITestOutputHelper _log;

    public Adr035ResidualTests(ITestOutputHelper log) => _log = log;

    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

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
            _log.WriteLine($"{s.Reason,-10} {s.Goal,-10} {s.File}:{s.Line}  vars=["
                + string.Join(", ", s.Variables.Select(v => $"{v.Name}={v.Value}"))
                + "]  residuals=["
                + string.Join("; ", s.Frames.Count > 0
                    ? s.Frames[0].Residuals.Select(r => $"{r.Name}: {r.Goals}")
                    : Enumerable.Empty<string>())
                + "]");
        return stops;
    }

    private static string ResidualOf(DebugStopEvent stop, string name) =>
        stop.Frames[0].Residuals.First(r => r.Name == name).Goals;

    [Fact]
    public void ClpfdDomainsShowAsResidualRows()
    {
        //   3: p(X, Y) :-
        //   4:     X in 1..9, X #< Y, Y in 3..7,
        //   5:     mark(X, Y).
        //   6: mark(_, _).
        var engine = DebugEngine(
            ":- use_module(library(clpfd)).\n"
            + "p(X, Y) :-\n    X in 1..9, X #< Y, Y in 3..7,\n    mark(X, Y).\nmark(_, _).\n");
        engine.AddBreakpoint("<string>", 5);

        var stop = Walk(engine, "p(A, B).")[0];

        string x = ResidualOf(stop, "X");
        Assert.Contains("in", x);
        Assert.Contains("X", x);
        // The cross-variable propagator is shown once, under its first variable, and it
        // names the SIBLING by the frame's own name — the transplant carried Y's
        // attributes along with X's.
        Assert.Contains("#<", x);
        Assert.Contains("Y", x);
        string y = ResidualOf(stop, "Y");
        Assert.Contains("in", y);
    }

    [Fact]
    public void DifAndFreezeShowAsResiduals()
    {
        var engine = DebugEngine(
            ":- use_module(library(coroutining)).\n"
            + "p(X, Y, Z) :-\n    dif(X, Y), freeze(Z, true),\n    mark(X, Y, Z).\nmark(_, _, _).\n");
        engine.AddBreakpoint("<string>", 5);

        var stop = Walk(engine, "p(A, B, C).")[0];

        var rows = stop.Frames[0].Residuals;
        Assert.Contains(rows, r => r.Goals.Contains("dif"));
        Assert.Contains(rows, r => r.Goals.Contains("freeze") || r.Goals.Contains("frozen"));
    }

    [Fact]
    public void AFrameWithNoAttributedVariablesCarriesNoResiduals()
    {
        var engine = DebugEngine("p(X) :-\n    X = plain,\n    mark(X).\nmark(_).\n");
        engine.AddBreakpoint("<string>", 3);

        var stop = Walk(engine, "p(A).")[0];

        Assert.Empty(stop.Frames[0].Residuals);
    }

    [Fact]
    public void AThrowingProjectionHookDegradesToNoConstraintsNotABrokenStop()
    {
        // attribute_goals/4 is the dynamic dispatcher every library joins; a hook that
        // throws must cost the constraints display, never the stop.
        var engine = DebugEngine(
            "p(X) :-\n    put_attr(X, boommod, payload),\n    mark(X).\nmark(_).\n");
        engine.QueryAll("assertz((attribute_goals(boommod, _, _, _) :- throw(boom))).")
            .ToList();
        engine.AddBreakpoint("<string>", 3);

        var stop = Walk(engine, "p(A).")[0];

        Assert.Equal(StopReason.Breakpoint, stop.Reason);
        // No usable projection — the row is simply absent.
        Assert.DoesNotContain(stop.Frames[0].Residuals, r => r.Goals.Contains("boom"));
    }

    [Fact]
    public void TheImmediateWindowSeesTheFrameVariablesConstraints()
    {
        var engine = DebugEngine(
            ":- use_module(library(clpfd)).\n"
            + "p(X) :-\n    X in 1..9,\n    mark(X).\nmark(_).\n");
        engine.AddBreakpoint("<string>", 5);

        string getAttr = "";
        string projected = "";
        string posted = "";
        var svc = new DebugService(engine, (s, e) =>
        {
            // get_attr/3 on a frame variable answers its real attribute.
            getAttr = s.EvaluateGoal(0, "get_attr(X, clpfd, A)");
            // copy_term/3 projects the frame variable's constraints.
            projected = s.EvaluateGoal(0, "copy_term(X, _C, G)");
            // Posting a NEW constraint narrows the transplanted copy (eval-local).
            posted = s.EvaluateGoal(0, "X #< 5.");
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        engine.QueryAll("p(V).").ToList();
        engine.AttachDebugSession(null);

        _log.WriteLine("get_attr -> " + getAttr);
        _log.WriteLine("copy_term/3 -> " + projected);
        _log.WriteLine("post -> " + posted);
        Assert.Contains("A = ", getAttr);            // bound to the fd attribute term
        Assert.Contains("in", projected);            // G = [_ in 1..9]
        Assert.DoesNotContain("existence_error", posted);
        Assert.DoesNotContain("false", posted);      // the post succeeded on the transplant
    }

    [Fact]
    public void ABreakpointConditionCanReadTheAttribute()
    {
        var engine = DebugEngine(
            ":- use_module(library(clpfd)).\n"
            + "p(X) :-\n    X in 1..9,\n    mark(X).\nmark(_).\n");
        engine.AddBreakpoint("<string>", 5, "get_attr(X, clpfd, _)");

        var stops = Walk(engine, "p(V).");

        Assert.Single(stops);
        Assert.Equal(StopReason.Breakpoint, stops[0].Reason);
        Assert.Equal("", stops[0].ConditionError);
    }

    [Fact]
    public void TheChannelCarriesResidualsThroughTheWire()
    {
        var engine = DebugEngine(
            ":- use_module(library(clpfd)).\n"
            + "p(X) :-\n    X in 1..9,\n    mark(X).\nmark(_).\n");
        engine.AddBreakpoint("<string>", 5);

        DebugSnapshot? decoded = null;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            // Read the way a debugger does: from the pinned address, as bytes.
            var bytes = new byte[DebugChannel.SnapshotCapacity];
            System.Runtime.InteropServices.Marshal.Copy(
                session!.Channel.SnapshotAddress, bytes, 0, bytes.Length);
            decoded = DebugChannel.ReadSnapshot(bytes);
        });
        using (session)
            engine.QueryAll("p(V).").ToList();

        Assert.NotNull(decoded);
        var frame = decoded!.Frames[0];
        _log.WriteLine("wire residuals: "
            + string.Join("; ", frame.Residuals.Select(r => $"{r.Name}: {r.Value}")));
        Assert.Contains(frame.Residuals, r => r.Name == "X" && r.Value.Contains("in"));
    }
}
