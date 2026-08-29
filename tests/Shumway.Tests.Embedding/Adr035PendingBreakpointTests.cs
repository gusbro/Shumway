using System;
using System.Collections.Generic;
using System.IO;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 D4 — a breakpoint set BEFORE the program is loaded.
///
/// <para>This is the ordinary case under a launch, and the only one: the user draws the red
/// dot, then presses the button. The file is consulted afterwards. Until D4 the engine bound
/// a breakpoint only against code that already existed, so one asked for against an empty
/// program bound nothing and was FORGOTTEN — and the program then ran to completion through
/// every breakpoint in it, with no error anywhere to say so.</para>
/// </summary>
[Collection("debugger")]
public class Adr035PendingBreakpointTests
{
    private static string WriteTemp(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "shumway_pending_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(path, text);
        return path;
    }

    private static PrologEngine DebugEngine()
    {
        var engine = new PrologEngine();
        engine.Flags.EmitDebugInfo = true;
        engine.Flags.DebugCodegen = true;
        engine.Flags.DebugLco = false;
        return engine;
    }

    [Fact]
    public void ABreakpointSetBeforeTheFileIsConsultedBindsWhenItArrives()
    {
        var engine = DebugEngine();
        string path = WriteTemp(
            "main :- tick(3, D), write(D), nl.\n" +
            "\n" +
            "tick(N, Doubled) :-\n" +
            "    Doubled is N * 2.\n");
        try
        {
            // Line 4 is `Doubled is N * 2.` — the body of tick/2, and nothing is loaded yet.
            Assert.Equal(0, engine.AddBreakpoint(path, 4));
            Assert.Empty(engine.Breakpoints);

            engine.ConsultFile(path);

            // The code has arrived. The breakpoint the user drew on it is now armed — without
            // anyone asking again, because nobody would: the debugger already asked.
            Assert.NotEmpty(engine.Breakpoints);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AndItActuallySTOPSTheProgramThatRunsAfterTheConsult()
    {
        var engine = DebugEngine();
        var stops = new List<DebugStopEvent>();
        var service = new DebugService(engine, (s, stop) =>
        {
            stops.Add(stop);
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(service);

        string path = WriteTemp(
            "main :- tick(3, _).\n" +
            "\n" +
            "tick(N, Doubled) :-\n" +
            "    Doubled is N * 2.\n");
        try
        {
            engine.AddBreakpoint(path, 4);
            engine.ConsultFile(path);

            foreach (var _ in engine.QueryAll("main.")) { }

            DebugStopEvent hit = Assert.Single(stops);
            Assert.Equal(StopReason.Breakpoint, hit.Reason);
            Assert.Equal(4, hit.BreakLine);
            Assert.Equal(path, hit.BreakFile);
        }
        finally
        {
            engine.AttachDebugSession(null);
            File.Delete(path);
        }
    }

    [Fact]
    public void RemovingAPendingBreakpointForgetsIt()
    {
        var engine = DebugEngine();
        string path = WriteTemp("tick(N, D) :-\n    D is N * 2.\n");
        try
        {
            engine.AddBreakpoint(path, 2);
            engine.RemoveBreakpoint(path, 2);
            engine.ConsultFile(path);
            Assert.Empty(engine.Breakpoints);   // it was asked for, and then it was not
        }
        finally
        {
            File.Delete(path);
        }
    }
}
