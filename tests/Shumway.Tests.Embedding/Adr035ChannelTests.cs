using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 phase D1 — the pinned-memory channel: the engine writes a stop into memory
/// whose address never moves, and a debugger reads it from outside.
///
/// <para>The tests read the snapshot back through <see cref="Marshal"/>, from the raw
/// address, rather than from the C# array that happens to be behind it. That is not
/// pedantry: it is the only way to test the thing that actually has to work. Concord
/// reads this with <c>DkmProcess.ReadMemory</c>, from another process, which knows
/// nothing about managed arrays — it has an address and a length. If the bytes at that
/// address are right, the debugger works; if they are only right when read as a C#
/// array, it does not.</para>
/// </summary>
public class Adr035ChannelTests
{
    private readonly ITestOutputHelper _log;

    public Adr035ChannelTests(ITestOutputHelper log) => _log = log;

    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    /// <summary>Reads the snapshot the way a debugger does: from the address, as bytes.
    /// </summary>
    private static DebugSnapshot ReadFromMemory(DebugChannel channel)
    {
        var bytes = new byte[DebugChannel.SnapshotCapacity];
        Marshal.Copy(channel.SnapshotAddress, bytes, 0, bytes.Length);
        DebugSnapshot? snapshot = DebugChannel.ReadSnapshot(bytes);
        Assert.NotNull(snapshot);
        return snapshot;
    }

    [Fact]
    public void TheDebuggerReadsTheStopOutOfMemory_WithoutRunningAnythingInTheDebuggee()
    {
        //   2: top(A) :-
        //   3:     mid(A, B),
        //   4:     use(B).
        //   5: mid(In, out(In)).
        //   6: use(_).
        var engine = DebugEngine(
            "top(A) :-\n    mid(A, B),\n    use(B).\nmid(In, out(In)).\nuse(_).\n");
        engine.AddBreakpoint("<string>", 5);   // inside mid/2

        DebugSnapshot? seen = null;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            // This is the debugger's moment: the process is stopped, and everything it
            // needs is already in memory. It runs nothing in the debuggee — it reads.
            seen = ReadFromMemory(session!.Channel);
        });
        using (session)
            engine.QueryAll("top(one).").ToList();

        Assert.NotNull(seen);
        _log.WriteLine($"{seen!.Reason} {seen.Goal} at {seen.File}:{seen.Line}");
        foreach (var f in seen.Frames)
            _log.WriteLine($"  {f.Name}/{f.Arity} {f.File}:{f.Line}  "
                + string.Join(", ", f.Variables.Select(v => $"{v.Name} = {v.Value}")));

        Assert.Equal(StopReason.Breakpoint, seen.Reason);
        Assert.Equal("mid/2", seen.Goal);
        Assert.Equal(5, seen.Line);

        // The whole stack, with the variables of every frame, came across as bytes. The query
        // the user launched this from is the bottom of every Prolog stack — named by the goal
        // they typed, and not a predicate (arity -1).
        Assert.Equal(new[] { "mid/2", "top/1", "?- top(one)/-1" },
            seen.Frames.Select(f => $"{f.Name}/{f.Arity}"));
        Assert.Equal("one", seen.Frames[0].Variables.First(v => v.Name == "In").Value);
        Assert.Equal("one", seen.Frames[1].Variables.First(v => v.Name == "A").Value);
    }

    [Fact]
    public void TheDebuggerStepsByWritingACommandBack()
    {
        var engine = DebugEngine(
            "top(A) :-\n    mid(A, B),\n    use(B).\nmid(In, out(In)).\nuse(_).\n");
        engine.AddBreakpoint("<string>", 3);   // the call to mid/2

        var stops = new List<DebugSnapshot>();
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            stops.Add(ReadFromMemory(session!.Channel));
            // Step over the first stop, then let it run.
            session.Channel.WriteCommands(new DebugCommand(
                stops.Count == 1 ? DebugCommandKind.StepOver : DebugCommandKind.Continue));
        });
        using (session)
            engine.QueryAll("top(one).").ToList();

        // Two stops: the breakpoint, then where the step over landed — the NEXT GOAL of the
        // clause being stepped through. The command went in as bytes, through memory, and the
        // engine obeyed it.
        Assert.Equal(new[] { StopReason.Breakpoint, StopReason.Call },
            stops.Select(s => s.Reason));
        Assert.Equal("use/1", stops[1].Goal);
    }

    [Fact]
    public void AStackTooBigForTheBufferIsTruncated_AndSaysHowManyFramesItReallyCarries()
    {
        // The Blint bug, in one test. A real program's stack is 239 frames deep and its
        // variables hold the file it is reading, so the stop did not fit — and the writer
        // wrote the TRUE frame count and then silently dropped what would not fit, leaving
        // the tail of an OLDER stop in the buffer behind it. The reader walked 239 frames
        // through bytes that were not frames, read an old string's bytes as a variable count,
        // and asked for a list of two billion. It died of an OutOfMemoryException inside the
        // stop handler, so the pause the user asked for was never completed and Visual Studio
        // waited for it for ever while the program ran on.
        using var channel = new DebugChannel();

        var big = new List<PrologEngine.DebugFrame>();
        for (int i = 0; i < 400; i++)
        {
            var variables = new List<(string, string)>();
            for (int v = 0; v < 8; v++)
                variables.Add(($"V{v}", new string('x', 500)));   // ~4 KB of variables a frame
            big.Add(new PrologEngine.DebugFrame($"deep{i}", 1, "big.pl", i, i, variables));
        }

        // Something WAS in the buffer before: the tail of a longer stop is exactly what the
        // reader used to walk into.
        channel.WriteSnapshot(new DebugStopEvent(
            StopReason.Breakpoint, "old/0", "big.pl", 1, 1, big));
        channel.WriteSnapshot(new DebugStopEvent(
            StopReason.AsyncBreak, "deep0/1", "big.pl", 0, 400, big));

        DebugSnapshot snapshot = ReadFromMemory(channel);
        _log.WriteLine($"{big.Count} frames offered, {snapshot.Frames.Count} carried");

        // Truncated — and honest about it: every frame it claims is really there, whole.
        Assert.True(snapshot.Frames.Count > 0);
        Assert.True(snapshot.Frames.Count < big.Count);
        Assert.Equal(StopReason.AsyncBreak, snapshot.Reason);
        for (int i = 0; i < snapshot.Frames.Count; i++)
        {
            Assert.Equal($"deep{i}", snapshot.Frames[i].Name);
            Assert.Equal(8, snapshot.Frames[i].Variables.Count);
            Assert.Equal(500, snapshot.Frames[i].Variables[0].Value.Length);
        }
    }

    [Fact]
    public void ACorruptCountIsNotBelieved()
    {
        // The other half of the same defence, at the reader. Whatever is in these bytes — an
        // engine of another version, a torn write, a buffer nobody has written yet — a count
        // read out of them is four bytes, not a promise, and the debugger must not size an
        // allocation from it.
        var bytes = new byte[1024];
        int at = 0;
        DebugWire.WriteInt(bytes, ref at, DebugWire.FormatVersion);
        DebugWire.WriteInt(bytes, ref at, 1);          // sequence
        DebugWire.WriteInt(bytes, ref at, 0);          // running
        DebugWire.WriteInt(bytes, ref at, 0);          // heartbeat
        DebugWire.WriteInt(bytes, ref at, 0);          // interop depth
        DebugWire.WriteInt(bytes, ref at, (int)StopReason.AsyncBreak);
        DebugWire.WriteString(bytes, ref at, "g/0");
        DebugWire.WriteString(bytes, ref at, "f.pl");
        DebugWire.WriteInt(bytes, ref at, 1);          // line
        DebugWire.WriteInt(bytes, ref at, 1);          // depth
        DebugWire.WriteString(bytes, ref at, "");
        DebugWire.WriteInt(bytes, ref at, 0);
        DebugWire.WriteInt(bytes, ref at, int.MaxValue);   // "two billion frames follow"

        DebugSnapshot? snapshot = DebugChannel.ReadSnapshot(bytes);   // must not throw
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot!.Frames);
        Assert.Equal(StopReason.AsyncBreak, snapshot.Reason);
    }

    [Fact]
    public void TheSequenceNumberRisesOnEveryStop_SoAMissedOneShows()
    {
        var engine = DebugEngine("p(1).\np(2).\np(3).\n");
        engine.AddBreakpoint("<string>", 2);
        engine.AddBreakpoint("<string>", 3);

        var sequences = new List<int>();
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine,
            notify: _ => sequences.Add(ReadFromMemory(session!.Channel).Sequence));
        using (session)
            engine.QueryAll("p(X).").ToList();

        Assert.Equal(new[] { 1, 2 }, sequences);
    }

    [Fact]
    public void AttachHandsOutTheAddresses_AndTheyAreTheOnesTheChannelUses()
    {
        var engine = DebugEngine("p(1).\n");
        using var session = new ChannelDebugSession(engine, notify: _ => { });

        string handshake = ShumwayDebugHelper.Attach();
        _log.WriteLine(handshake);

        // "v1;snapshot=<hex>,<len>;commands=<hex>,<len>" — the one func-eval the design
        // allows, and it happens at attach, where evaluating a function is safe.
        Assert.StartsWith($"v{DebugChannel.FormatVersion};", handshake);
        long snapshotAddr = Convert.ToInt64(
            handshake.Split("snapshot=")[1].Split(',')[0], 16);
        Assert.Equal(session.Channel.SnapshotAddress.ToInt64(), snapshotAddr);
        Assert.Contains($",{DebugChannel.SnapshotCapacity};", handshake);

        Assert.Equal(DebugChannel.FormatVersion, ShumwayDebugHelper.Ping());
    }

    [Fact]
    public void AnAsynchronousBreakAsksTheEngineWhereItIs()
    {
        // Break All lands wherever the machine happens to be — at no port, so nothing
        // has been reported, and the channel holds whatever the last stop left. Showing
        // that would be a lie. So the debugger asks, and the engine answers from the
        // machine that was last running. This is the one thing the port model cannot do
        // on its own, and the reason DebugService.Current outlives a stop.
        var engine = DebugEngine(
            "top(A) :-\n    mid(A, B),\n    use(B).\nmid(In, out(In)).\nuse(_).\n");
        engine.AddBreakpoint("<string>", 5);   // inside mid/2

        DebugSnapshot? onBreakAll = null;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            // Stand in for the user hitting Break All a moment later: ask, then read
            // exactly what a debugger would read, from the address.
            int sequence = ShumwayDebugHelper.CaptureNow();
            Assert.True(sequence > 0);
            onBreakAll = ReadFromMemory(session!.Channel);
        });
        using (session)
            engine.QueryAll("top(one).").ToList();

        Assert.NotNull(onBreakAll);
        Assert.Equal(StopReason.AsyncBreak, onBreakAll!.Reason);
        // Not a port — but the stack is real, and so are the variables.
        Assert.Equal(new[] { "mid/2", "top/1", "?- top(one)/-1" },
            onBreakAll.Frames.Select(f => $"{f.Name}/{f.Arity}"));
        Assert.Equal("one", onBreakAll.Frames[0].Variables.First(v => v.Name == "In").Value);
    }

    [Fact]
    public void AnAsynchronousBreakWithNothingRunningSaysSo()
    {
        var engine = DebugEngine("p(1).\n");
        using var session = new ChannelDebugSession(engine, notify: _ => { });

        // Between queries there is no machine to ask, and inventing one would be worse
        // than saying nothing.
        Assert.Equal(0, ShumwayDebugHelper.CaptureNow());
    }

    [Fact]
    public void AttachSaysSoWhenNoSessionIsRunning()
    {
        ShumwayDebugHelper.Channel = null;
        Assert.Equal("", ShumwayDebugHelper.Attach());
    }

    [Fact]
    public void ACommandIsObeyedOnce()
    {
        using var channel = new DebugChannel();
        channel.WriteCommands(
            new DebugCommand(DebugCommandKind.AddBreakpoint, "foo.pl", 12),
            new DebugCommand(DebugCommandKind.StepInto));

        var drained = channel.DrainCommands();
        Assert.Equal(2, drained.Count);
        Assert.Equal(DebugCommandKind.AddBreakpoint, drained[0].Kind);
        Assert.Equal("foo.pl", drained[0].File);
        Assert.Equal(12, drained[0].Line);
        Assert.Equal(DebugCommandKind.StepInto, drained[1].Kind);

        // Draining empties the region: a step the debugger asked for once must not be
        // taken again at the next stop.
        Assert.Empty(channel.DrainCommands());
    }

    [Fact]
    public void TheDebuggerCanTurnLastCallOptimisationOffMidQuery()
    {
        // What debug_lastcall being an opcode that reads a flag — rather than a decision
        // baked in at compile time — is FOR. The debugger arrives, finds a flat stack,
        // and asks for the frames back without recompiling or restarting anything.
        var engine = DebugEngine(
            "top(X) :-\n    mid(X).\nmid(X) :-\n    leaf(X).\nleaf(7).\n");
        engine.QueryAll("set_prolog_flag(debug_lco, on).").ToList();   // LCO on: flat
        engine.AddBreakpoint("<string>", 6);   // inside leaf/1

        var depths = new List<int>();
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            depths.Add(ReadFromMemory(session!.Channel).Frames.Count);
            session.Channel.WriteCommands(
                new DebugCommand(DebugCommandKind.SetLastCallOptimisation, Flag: false),
                new DebugCommand(DebugCommandKind.Continue));
        });
        using (session)
        {
            engine.QueryAll("top(A).").ToList();   // stops with LCO on: callers gone
            engine.QueryAll("top(A).").ToList();   // and again, now that it is off
        }

        _log.WriteLine($"frames: {string.Join(" then ", depths)}");
        Assert.Equal(1, depths[0]);   // leaf/1 alone — LCO reclaimed mid/1, top/1 and the query
        Assert.Equal(4, depths[1]);   // leaf/1, mid/1, top/1, ?- — the whole stack
    }
}
