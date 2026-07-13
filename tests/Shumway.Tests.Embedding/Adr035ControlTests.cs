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
        var engine = DebugEngine(
            "top(A) :-\n    mid(A, B),\n    use(B).\nmid(In, out(In)).\nuse(_).\n");

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
        var engine = DebugEngine(
            "top(A) :-\n    mid(A, B),\n    use(B).\nmid(In, out(In)).\nuse(_).\n");

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
        var engine = DebugEngine("loop(N) :- N > 0, tick(N), M is N - 1, loop(M).\nloop(0).\ntick(_).\n");

        DebugSnapshot? seen = null;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ => seen = ReadFromMemory(session!.Channel));

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

    [Fact]
    public void AStepWrittenAtAStopIsTakenOnce()
    {
        var engine = DebugEngine(
            "top(A) :-\n    mid(A, B),\n    use(B).\nmid(In, out(In)).\nuse(_).\n");
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
        Assert.Equal("mid/2", stops[1].Goal);               // stepped INTO the goal
    }
}
