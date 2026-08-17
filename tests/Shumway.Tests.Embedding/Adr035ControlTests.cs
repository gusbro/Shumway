using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 phase D3 — the control side, tested from the engine's end of the channel.
///
/// <para>The Concord components are the other end, and they are not here. What is here is
/// everything they depend on: that a breakpoint the user set on a rule's HEAD reports back
/// the head line (so a debugger can match a hit to the red dot it drew, rather than to the
/// line the code turned out to be on); that removing it from that same line works; and that
/// a breakpoint can be armed on a program that is ALREADY RUNNING, which is what F9 during
/// a long query is and which nothing before D3 could do.</para>
/// </summary>
public class Adr035ControlTests
{
    private readonly ITestOutputHelper _log;

    public Adr035ControlTests(ITestOutputHelper log) => _log = log;

    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    private static DebugSnapshot ReadFromMemory(DebugChannel channel)
    {
        var bytes = new byte[DebugChannel.SnapshotCapacity];
        Marshal.Copy(channel.SnapshotAddress, bytes, 0, bytes.Length);
        DebugSnapshot? snapshot = DebugChannel.ReadSnapshot(bytes);
        Assert.NotNull(snapshot);
        return snapshot;
    }

    /// <summary>Writes into the command region the way the debugger does: as bytes, at the
    /// address, in one shot.</summary>
    private static void WriteCommandsToMemory(DebugChannel channel, params DebugWireCommand[] commands)
    {
        byte[] bytes = DebugWire.EncodeCommands(commands);
        Marshal.Copy(bytes, 0, channel.CommandAddress, bytes.Length);
    }

    [Fact]
    public void AHitNamesTheBreakpointTheUserDrew_NotTheLineItBoundTo()
    {
        //   2: top(A) :-
        //   3:     mid(A, B),
        //   4:     use(B).
        var engine = DebugEngine("""
            top(A) :-
                mid(A, B),
                use(B).
            mid(In, out(In)).
            use(_).
            """);

        // F9 on the HEAD. There is no code there — a rule's entry point IS its first
        // goal — so it binds forward to line 3.
        Assert.Equal(3, engine.BoundLine("<string>", 2));
        Assert.True(engine.AddBreakpoint("<string>", 2) > 0);

        DebugSnapshot? seen = null;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ => seen = ReadFromMemory(session!.Channel));
        using (session)
            engine.QueryAll("top(one).").ToList();

        Assert.NotNull(seen);
        _log.WriteLine($"stop at {seen!.File}:{seen.Line}, breakpoint {seen.BreakFile}:{seen.BreakLine}");

        // WHICH BREAKPOINT fired: line 2, where the red dot is. Matching a hit against
        // line 3 would find no breakpoint at all, and the debugger would run straight past
        // the one thing the user asked for by name.
        Assert.Equal(StopReason.Breakpoint, seen.Reason);
        Assert.Equal(2, seen.BreakLine);
        Assert.Equal("<string>", seen.BreakFile);

        // WHERE THE MACHINE IS: line 3, the first goal — which is what the editor's caret
        // follows. Two different questions, two different answers, and a debugger needs
        // both.
        Assert.Equal(3, seen.Line);
        Assert.Equal(3, seen.Frames[0].Line);
    }

    [Fact]
    public void ABreakpointIsRemovedFromTheLineItWasSetOn()
    {
        var engine = DebugEngine("""
            top(A) :-
                mid(A, B),
                use(B).
            mid(In, out(In)).
            use(_).
            """);

        engine.AddBreakpoint("<string>", 2);      // set on the head...
        Assert.NotEmpty(engine.Breakpoints);
        engine.RemoveBreakpoint("<string>", 2);   // ...removed from the head
        Assert.Empty(engine.Breakpoints);

        int stops = 0;
        using (new ChannelDebugSession(engine, notify: _ => stops++))
            engine.QueryAll("top(one).").ToList();

        Assert.Equal(0, stops);
    }

    [Fact]
    public void ABreakpointCanBeArmedOnAProgramThatIsAlreadyRunning()
    {
        //   2: loop(N) :- N > 0, tick(N), M is N - 1, loop(M).
        //   3: loop(0).
        //   4: tick(_).
        var engine = DebugEngine("""
            loop(N) :- N > 0, tick(N), M is N - 1, loop(M).
            loop(0).
            tick(_).
            """);

        DebugSnapshot? seen = null;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            if (seen is not null) return;
            seen = ReadFromMemory(session!.Channel);
            // One hit proves the point. Clear the breakpoint from inside the stop
            // (the drain right after notify applies it) so the remaining ~2000
            // iterations run free — with debug_lco off each later hit pays a
            // stop + frame walk over an ever-deeper stack, and under a loaded
            // machine the full run blew past the gate's hang timeout.
            WriteCommandsToMemory(session.Channel,
                new DebugWireCommand { Kind = DebugCommandKind.ClearBreakpoints });
        });

        using (session)
        {
            // The debugger writes the breakpoint into the channel with the engine RUNNING —
            // no stop, nobody draining. Before D3 nothing would ever have read it: the
            // engine only looked at the channel while it was stopped, so a breakpoint set
            // during a query could not take effect until the next one.
            WriteCommandsToMemory(session.Channel,
                new DebugWireCommand
                {
                    Kind = DebugCommandKind.AddBreakpoint,
                    File = "<string>",
                    Line = 4,   // inside tick/1
                });

            // Long enough for the poll (every DebugService.PollInterval ports) to come round.
            engine.QueryAll("loop(2000).").ToList();
        }

        Assert.NotNull(seen);
        _log.WriteLine($"{seen!.Reason} {seen.Goal} at {seen.File}:{seen.Line}");
        Assert.Equal(StopReason.Breakpoint, seen.Reason);
        Assert.Equal("tick/1", seen.Goal);
    }

    /// <summary>The bug the first Visual Studio run found, and which nothing in this suite
    /// could have: every test here consults a STRING, so every stop honestly said it came
    /// from <c>&lt;string&gt;</c> — and so did every clause of every program consulted from a
    /// real file. Compilation happens at query setup, long after the consult that read the
    /// file is over, and the compiler was being told "the file we are reading now", which by
    /// then was nobody's file at all. In VS the symptom was total: not one breakpoint could
    /// ever bind, because no clause admitted to being in the file the user had open.</summary>
    [Fact]
    public void AClauseConsultedFromAFileKnowsWhichFileItCameFrom()
    {
        string path = Path.Combine(Path.GetTempPath(), "shumway_adr035_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(path, "top(A) :-\n    mid(A, B),\n    use(B).\nmid(In, out(In)).\nuse(_).\n");
        try
        {
            var engine = new PrologEngine();
            engine.Flags.EmitDebugInfo = true;
            engine.Flags.DebugCodegen = true;
            engine.ConsultFile(path);
            engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

            Assert.True(engine.AddBreakpoint(path, 4) > 0);   // inside mid/2

            DebugSnapshot? seen = null;
            ChannelDebugSession? session = null;
            session = new ChannelDebugSession(engine, notify: _ => seen = ReadFromMemory(session!.Channel));
            using (session)
                engine.QueryAll("top(one).").ToList();

            Assert.NotNull(seen);
            _log.WriteLine($"{seen!.Reason} {seen.Goal} at {seen.File}:{seen.Line}");
            Assert.Equal(StopReason.Breakpoint, seen.Reason);
            Assert.Equal(path, seen.File);
            Assert.Equal(path, seen.Frames[0].File);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The Blint bug the user hit in real VS: stop at a breakpoint, DISABLE it, press
    /// Continue → "break opcode at PC=0x… with no breakpoint recorded — the code space and the
    /// breakpoint table are out of step." Root cause: a mid-query <c>assertz</c> (Blint's
    /// directive processing does exactly this) grows the persistent buffer, so the running
    /// activation switches to a new array with the Break bytes copied in — but
    /// <c>_patchedProgram</c>, the buffer <c>SyncBreakpoints</c> restores, kept pointing at the
    /// abandoned one. Removing the breakpoint restored the DEAD buffer and cleared the table
    /// while the live buffer kept the Break byte; the next call re-hit it with an empty table
    /// and threw.
    ///
    /// <para>This is the portable OWNER-path shape (<c>seen/2</c> declared dynamic, so the
    /// asserts stay on the host buffer). <see cref="Adr035BlintDisableBp"/> covers the non-owner
    /// path against the real program.</para></summary>
    [Fact]
    public void RemovingABreakpointAfterAMidQueryBufferGrowth_KeepsTheCodeSpaceInStep()
    {
        //   2: :- dynamic(seen/2).
        //   3: run :- grow(4000), each(4).
        //   4: grow(0) :- !.
        //   5: grow(N) :- N > 0, assertz(seen(N, [N,N,N,N,N,N,N,N,N,N])), M is N - 1, grow(M).
        //   6: each(0) :- !.
        //   7: each(N) :- N > 0, step(N), M is N - 1, each(M).
        //   8: step(N) :- work(N).
        //   9: work(_).
        //
        // grow/1 appends thousands of chunky dynamic clauses — far more than the persistent
        // buffer's doubling slack (which scales with the baked prelude), so a reallocation is
        // guaranteed BEFORE the breakpoint in step/1 is first hit.
        var engine = DebugEngine("""
            :- dynamic(seen/2).
            run :- grow(4000), each(4).
            grow(0) :- !.
            grow(N) :- N > 0, assertz(seen(N, [N,N,N,N,N,N,N,N,N,N])), M is N - 1, grow(M).
            each(0) :- !.
            each(N) :- N > 0, step(N), M is N - 1, each(M).
            step(N) :- work(N).
            work(_).
            """);

        Assert.True(engine.AddBreakpoint("<string>", 8) > 0);   // inside step/1

        int stops = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            stops++;
            if (stops == 1) engine.RemoveBreakpoint("<string>", 8);   // the "disable"
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);

        List<Solution> solutions;
        try
        {
            solutions = engine.QueryAll("run.").ToList();   // used to throw on the 2nd step/1 hit
        }
        finally
        {
            engine.AttachDebugSession(null);
        }

        Assert.Single(solutions);   // ran to completion, no "out of step"
        Assert.Equal(1, stops);     // hit once; after removal the following step/1 calls are clean
    }

    [Fact]
    public void AStepWrittenAtAStopIsTakenOnce()
    {
        var engine = DebugEngine("""
            top(A) :-
                mid(A, B),
                use(B).
            mid(In, out(In)).
            use(_).
            """);
        engine.AddBreakpoint("<string>", 3);

        var stops = new List<DebugSnapshot>();
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            stops.Add(ReadFromMemory(session!.Channel));
            // The debugger answers ONE step. If a command survived its drain, the engine
            // would step for ever without being asked again.
            if (stops.Count == 1)
                WriteCommandsToMemory(session.Channel,
                    new DebugWireCommand { Kind = DebugCommandKind.StepInto });
        });

        using (session)
            engine.QueryAll("top(one).").ToList();

        _log.WriteLine(string.Join("\n", stops.Select(s => $"{s.Reason} {s.Goal} :{s.Line}")));
        Assert.Equal(2, stops.Count);                       // the breakpoint, then the step
        Assert.Equal(StopReason.Breakpoint, stops[0].Reason);

        // Stopped on the goal mid(A, B) and stepped into it — and mid/2 is a FACT, so there
        // is nothing inside it to be in. The step lands on the next goal of the clause,
        // use(B), which is where the program actually goes. (It used to land on mid/2's exit
        // port, which put the caret on mid/2's own clause: not where the program is going,
        // and not a line the user was stepping through.)
        Assert.Equal("use/1", stops[1].Goal);
    }
}
