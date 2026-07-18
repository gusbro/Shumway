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
                // DISTINCT content per variable — equal strings would (rightly) share one
                // string-table entry and the whole stack would fit. Truncation is for the
                // stack that genuinely does not.
                variables.Add(($"V{v}", new string('x', 500) + i + "_" + v));
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
            Assert.Equal(new string('x', 500) + i + "_0", snapshot.Frames[i].Variables[0].Value);
        }
    }

    [Fact]
    public void AValueSharedByManyFramesIsSerializedOnce()
    {
        // The bag, observed from the outside. A call stack is mostly the same bindings seen
        // from different clauses -- a 200-frame recursion sharing one big term is the shape
        // Blint pauses in -- and per-frame serialization made the snapshot's size the value's
        // size TIMES the depth: 200 x 5 KB would not fit in the 256 KB channel, and the stack
        // would (honestly, but needlessly) truncate. With the string table it is the value's
        // size PLUS the depth, so the whole stack fits with room to spare.
        using var channel = new DebugChannel();

        string shared = new string('d', 5000);   // ~5 KB, the same INSTANCE in every frame
        var frames = new List<PrologEngine.DebugFrame>();
        for (int i = 0; i < 200; i++)
            frames.Add(new PrologEngine.DebugFrame("down", 1, "deep.pl", i, i,
                new[] { ("Data", shared), ("N", i.ToString()) }));

        channel.WriteSnapshot(new DebugStopEvent(
            StopReason.Breakpoint, "down/1", "deep.pl", 0, 200, frames));

        DebugSnapshot snapshot = ReadFromMemory(channel);
        _log.WriteLine($"{snapshot.Frames.Count} frames carried");

        // Nothing was dropped -- 200 x 5 KB never happened -- and every frame still answers
        // with the whole value. The instance is shared on the reading side too: that is what
        // the indirection is FOR, and it is also the honest test that one entry backs them all.
        Assert.Equal(200, snapshot.Frames.Count);
        Assert.All(snapshot.Frames, f =>
            Assert.Equal(shared, f.Variables[0].Value));
        Assert.All(snapshot.Frames, f =>
            Assert.Same(snapshot.Frames[0].Variables[0].Value, f.Variables[0].Value));
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
        DebugWire.WriteString(bytes, ref at, "");          // breakFile
        DebugWire.WriteInt(bytes, ref at, 0);              // breakLine
        DebugWire.WriteString(bytes, ref at, "");          // conditionError
        DebugWire.WriteInt(bytes, ref at, 0);              // setNextLines count (empty)
        DebugWire.WriteInt(bytes, ref at, 0);              // an empty string table
        DebugWire.WriteInt(bytes, ref at, int.MaxValue);   // "two billion frames follow"

        DebugSnapshot? snapshot = DebugChannel.ReadSnapshot(bytes);   // must not throw
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot!.Frames);
        Assert.Equal(StopReason.AsyncBreak, snapshot.Reason);

        // And the same lie told about the STRING TABLE dies just as quietly.
        int back = at - 8;
        DebugWire.WriteInt(bytes, ref back, int.MaxValue);   // "two billion strings follow"
        snapshot = DebugChannel.ReadSnapshot(bytes);
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot!.Frames);
    }

    [Fact]
    public void AStepTakenAtABreakAllStops_HoweverDeepThePauseLanded()
    {
        // THE REPORT: Break All deep in a long program, the stack looks right, F10 -- and it
        // runs to completion without ever stopping again.
        //
        // A step is measured against the depth of the stop it was taken AT, and an
        // asynchronous break did not record its depth: it comes through its own path (the
        // poll between goals), not through the service's Stop(). So the F10 was measured
        // against whatever the last REAL stop left behind -- or zero, if there had never been
        // one -- and paused 60 frames deep it waited for a port at depth <= 0. No port
        // qualifies; the program runs out; the step is abandoned. If the machine is shallower
        // than where the step was taken, the step MUST stop.
        var engine = DebugEngine(
            "down(0) :- !.\ndown(N) :-\n    N > 0,\n    N1 is N - 1,\n    down(N1),\n    tick(N).\ntick(_).\n");

        var stops = new List<DebugSnapshot>();
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            stops.Add(ReadFromMemory(session!.Channel));
            session.Channel.WriteCommands(new DebugCommand(
                stops.Count == 1 ? DebugCommandKind.StepOver : DebugCommandKind.Continue));
        });
        using (session)
        {
            // Pause it mid-run: the command is already in the channel when the query starts,
            // so the poll finds it a few hundred goals in -- genuinely deep in the recursion.
            session.Channel.WriteCommands(new DebugCommand(DebugCommandKind.BreakNow));
            engine.QueryAll("down(600).").ToList();
        }

        _log.WriteLine(string.Join("\n", stops.Select(s => $"{s.Reason} {s.Goal} depth={s.Depth}")));

        // The pause landed at a port, deep; the step taken there STOPPED -- at the next goal,
        // at or above the pause's depth -- instead of letting the program run to its end.
        Assert.Equal(StopReason.AsyncBreak, stops[0].Reason);
        Assert.True(stops[0].Depth > 10, $"the pause should land deep, landed at {stops[0].Depth}");
        Assert.True(stops.Count >= 2, "the step never stopped: the program ran to completion");
        Assert.Equal(StopReason.Call, stops[1].Reason);
        Assert.True(stops[1].Depth <= stops[0].Depth,
            $"the step went {stops[0].Depth} -> {stops[1].Depth}");
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
        Assert.Equal("", drained[0].Condition);   // unconditional: the field crosses empty
        Assert.Equal(DebugCommandKind.StepInto, drained[1].Kind);

        // Draining empties the region: a step the debugger asked for once must not be
        // taken again at the next stop.
        Assert.Empty(channel.DrainCommands());
    }

    [Fact]
    public void ABreakpointConditionCrossesTheChannel()
    {
        // ADR-035 D5 — the condition rides the AddBreakpoint command; both directions of
        // the codec must agree on it, or a conditional breakpoint set from Visual Studio
        // silently arrives unconditional.
        using var channel = new DebugChannel();
        channel.WriteCommands(
            new DebugCommand(DebugCommandKind.AddBreakpoint, "foo.pl", 12,
                Condition: "X > 3, interesting(X)"));

        var drained = channel.DrainCommands();
        Assert.Single(drained);
        Assert.Equal("X > 3, interesting(X)", drained[0].Condition);
    }

    [Fact]
    public void AConditionErrorCrossesTheSnapshot()
    {
        // The stop that reports a condition that could not run carries WHY — the debugger
        // shows it, since silence would swallow the breakpoint undiagnosably.
        using var channel = new DebugChannel();
        channel.WriteSnapshot(new DebugStopEvent(
            StopReason.Breakpoint, "use/1", "foo.pl", 4, 3,
            System.Array.Empty<PrologEngine.DebugFrame>())
        {
            ConditionError = "breakpoint condition syntax error: unexpected ')'",
        });

        var snapshot = ReadFromMemory(channel);
        Assert.Equal("breakpoint condition syntax error: unexpected ')'",
            snapshot.ConditionError);

        // And an ordinary stop crosses with it empty.
        channel.WriteSnapshot(new DebugStopEvent(
            StopReason.Breakpoint, "use/1", "foo.pl", 4, 3,
            System.Array.Empty<PrologEngine.DebugFrame>()));
        Assert.Equal("", ReadFromMemory(channel).ConditionError);
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

    [Fact]
    public void ADetachedDebuggerLeavesNoArmedBreakpointsBehind()
    {
        // The user's report: stop at a breakpoint, DETACH (or close Visual Studio) — the
        // program runs on, but every subsequent hit still ran the whole stop pipeline
        // (capture, snapshot, notify to nobody) and scrolled "breakpoint hit ... stop"
        // forever. A stop on the REAL transport with no native debugger attached means the
        // debugger LEFT: the session clears the armed breakpoints and the program runs
        // free. (The breakpoints are Visual Studio's; a re-attach re-sends them all.)
        //
        // This test IS the real-transport shape: a default-notify session in a test
        // process has no native debugger — exactly what a detached debuggee looks like —
        // so the FIRST hit takes the detach path and disarms everything.
        //
        //  2: run :-
        //  3:     between(1, 50, X),
        //  4:     use(X),
        //  5:     fail.
        //  6: run.
        //  7: use(_).
        var engine = DebugEngine(
            "run :-\n    between(1, 50, X),\n    use(X),\n    fail.\nrun.\nuse(_).\n");
        Assert.True(engine.AddBreakpoint("<string>", 4) > 0);

        using (var session = new ChannelDebugSession(engine))   // default notify: detach-aware
        {
            var sols = engine.QueryAll("run.").ToList();
            Assert.Single(sols);                                // ran to completion, unblocked
        }

        // The first orphaned stop cleared every armed breakpoint — 49 later hits never
        // entered the stop pipeline at all.
        Assert.Empty(engine.Breakpoints);
    }

    [Fact]
    public void AConditionalBreakpointSetThroughTheChannel_StopsOnlyWhenItHolds()
    {
        // ADR-035 D5, end to end the way Visual Studio drives it: the debugger writes its
        // commands while the engine is STOPPED (the engine drains them before resuming),
        // and the condition rides the AddBreakpoint command — the same-key rewrite is how a
        // condition is set, changed and cleared. The engine evaluates it at the Break, and
        // only the hits where it holds reach the notify afterwards.
        var engine = DebugEngine(
            "run :-\n    between(1, 5, X),\n    use(X),\n    fail.\nrun.\nuse(_).\n");
        engine.AddBreakpoint("<string>", 4);   // unconditional at first, like a fresh F9

        var stops = new List<DebugSnapshot>();
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            stops.Add(ReadFromMemory(session!.Channel));
            // At the FIRST stop the user opens the breakpoint's settings and types the
            // condition: the debugger rewrites the breakpoint, condition attached.
            session!.Channel.WriteCommands(
                stops.Count == 1
                    ? new[]
                    {
                        new DebugCommand(DebugCommandKind.AddBreakpoint, "<string>", 4,
                            Condition: "X > 3"),
                        new DebugCommand(DebugCommandKind.Continue),
                    }
                    : new[] { new DebugCommand(DebugCommandKind.Continue) });
        });
        using (session)
            engine.QueryAll("run.").ToList();

        // X = 1 stopped unconditionally; the condition then filtered X = 2, 3 out.
        Assert.Equal(3, stops.Count);
        Assert.All(stops, s => Assert.Equal(StopReason.Breakpoint, s.Reason));
        Assert.All(stops, s => Assert.Equal("", s.ConditionError));
    }

    [Fact]
    public void AFramedClauseShowsOneFrame_AfterAPredicateCallReturnedMidBody()
    {
        // The user's report (prueba.pl fuzzy/0): after stepping past member/2, the call
        // stack showed fuzzy/0 TWICE — the current goal plus a ghost at the goal that had
        // already returned. Root cause: the frame walk yielded the Cp REGISTER, which
        // between two calls of a body still holds the PREVIOUS completed call's return
        // address (a real predicate call sets Cp where a builtin does not — so it took a
        // two-clause helper exiting with its choice point alive to expose it). With the
        // fix, the live walk takes the environment chain's saved continuations only.
        //
        //  2: run :- helper(X), use(X), done.     [3=helper 4=use 5=done — one per line]
        //  6: helper(1).
        //  7: helper(2).
        //  8: use(_).
        //  9: done.
        var engine = DebugEngine(
            "run :-\n    helper(X),\n    use(X),\n    done.\nhelper(1).\nhelper(2).\nuse(_).\ndone.\n");
        engine.AddBreakpoint("<string>", 4);   // use(X) — helper returned, its CP alive

        DebugSnapshot? snap = null;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            snap ??= ReadFromMemory(session!.Channel);
            session!.Channel.WriteCommands(new DebugCommand(DebugCommandKind.Continue));
        });
        using (session)
            engine.QueryAll("run.").ToList();

        Assert.NotNull(snap);
        foreach (var f in snap!.Frames)
            _log.WriteLine($"frame: {f.Name}/{f.Arity} at line {f.Line}");
        // Exactly one run/0 frame (at the current goal), then the query — no ghost.
        Assert.Equal(2, snap.Frames.Count);
        Assert.Equal("run", snap.Frames[0].Name);
        Assert.Equal(4, snap.Frames[0].Line);
        Assert.StartsWith("?-", snap.Frames[1].Name);
    }

    [Fact]
    public void PatchStopLine_RewritesTheStopAndTopFrameLine()
    {
        // ADR-035 D5+ — the in-place line patch that moves VS's arrow the instant of
        // Ctrl+Shift+F10 (the engine's real move is deferred to resume). Encode a stop with
        // a frame, patch the line, decode: both the stop line and the top frame's line are
        // the new value, everything else intact.
        using var channel = new DebugChannel();
        var frame = new PrologEngine.DebugFrame("go", 1, "f.pl", 10, 0,
            new[] { ("X", "42") }) { HeadArgs = "", ClauseNumber = 1 };
        channel.WriteSnapshot(new DebugStopEvent(
            StopReason.Breakpoint, "go/1", "f.pl", 10, 3, new[] { frame })
        {
            SetNextLines = new[] { 11, 12 },
        });

        var bytes = new byte[DebugChannel.SnapshotCapacity];
        System.Runtime.InteropServices.Marshal.Copy(channel.SnapshotAddress, bytes, 0, bytes.Length);
        Assert.True(DebugWire.TryPatchStopLine(bytes, 12));

        DebugSnapshot? s = DebugWire.ReadSnapshot(bytes);
        Assert.NotNull(s);
        Assert.Equal(12, s!.Line);                     // stop line moved
        Assert.Equal(12, s.Frames[0].Line);            // top frame line moved
        Assert.Equal("go", s.Frames[0].Name);          // everything else intact
        Assert.Equal(new[] { 11, 12 }, s.SetNextLines.ToArray());
        Assert.Equal("42", s.Frames[0].Variables[0].Value);
    }

    [Fact]
    public void TheSnapshotCarriesTheValidSetNextLines()
    {
        // ADR-035 D5+ — the debugger validates Ctrl+Shift+F10 synchronously off the
        // snapshot (it cannot func-eval to ask), so every stop must publish which lines
        // Set Next Statement accepts. With the prepended compile_mode line the program's
        // own lines are: 4=run head, 5=one(A), 6=two(B), 7=three(C), 8=Out=t(...). Stopped
        // at line 7 (three's call, one+two already ran): forward = 8, backward = 5 and 6
        // (their marks are live), the CURRENT line 7 is a no-op accept (as in C#), and the
        // head line 4 is offered because the first goal is reachable.
        var engine = DebugEngine(
            ":- dynamic(c/1).\nc(0).\n" +
            "run(Out) :-\n    one(A),\n    two(B),\n    three(C),\n    Out = t(A, B, C).\n" +
            "one(A) :- retract(c(A0)), A is A0 + 1, assertz(c(A)).\ntwo(20).\nthree(30).\n");
        engine.AddBreakpoint("<string>", 7);   // three(C) — one and two have run

        DebugSnapshot? snap = null;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            snap ??= ReadFromMemory(session!.Channel);
            session!.Channel.WriteCommands(new DebugCommand(DebugCommandKind.Continue));
        });
        using (session)
            engine.QueryAll("run(Out).").ToList();

        Assert.NotNull(snap);
        _log.WriteLine("valid SNS lines: " + string.Join(", ", snap!.SetNextLines));
        Assert.Contains(8, snap.SetNextLines);   // forward: Out = ...
        Assert.Contains(5, snap.SetNextLines);   // backward: one(A), mark live
        Assert.Contains(6, snap.SetNextLines);   // backward: two(B), mark live
        Assert.Contains(7, snap.SetNextLines);   // the current line: no-op accept
        Assert.Contains(4, snap.SetNextLines);   // the head: first goal reachable
    }

    [Fact]
    public void StoppedAtTheFirstGoal_TheHeadAndCurrentLineAreValidTargets()
    {
        // The user's report (prueba.pl fuzzy/0): stopped at the FIRST body goal, Set Next
        // Statement to the head line — or to the very line the arrow is on — was refused
        // ("valid targets: 18, 19, ...", forward only). Both are no-op moves and must be
        // offered, exactly as C# accepts a jump to the method's first line or the current
        // one. Same program as above, breakpoint on line 5 (one(A), the first goal —
        // nothing has run in the clause yet): head 4 and current 5 are valid, plus all
        // forward lines; nothing else.
        var engine = DebugEngine(
            ":- dynamic(c/1).\nc(0).\n" +
            "run(Out) :-\n    one(A),\n    two(B),\n    three(C),\n    Out = t(A, B, C).\n" +
            "one(A) :- retract(c(A0)), A is A0 + 1, assertz(c(A)).\ntwo(20).\nthree(30).\n");
        engine.AddBreakpoint("<string>", 5);

        DebugSnapshot? snap = null;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            snap ??= ReadFromMemory(session!.Channel);
            session!.Channel.WriteCommands(new DebugCommand(DebugCommandKind.Continue));
        });
        using (session)
            engine.QueryAll("run(Out).").ToList();

        Assert.NotNull(snap);
        _log.WriteLine("valid SNS lines at first goal: " + string.Join(", ", snap!.SetNextLines));
        Assert.Contains(4, snap.SetNextLines);   // head: no-op restart of an unstarted body
        Assert.Contains(5, snap.SetNextLines);   // current line: no-op
        Assert.Contains(6, snap.SetNextLines);
        Assert.Contains(7, snap.SetNextLines);
        Assert.Contains(8, snap.SetNextLines);
    }

    [Fact]
    public void SetNextStatementThroughTheChannel_MovesThePointerOnResume()
    {
        // ADR-035 D5+, the way Ctrl+Shift+F10 actually drives it: while stopped the engine
        // thread is parked in the notify, so the move CANNOT be a func-eval (a monitor-side
        // one answers "not implemented" — the popup the user hit). It rides the command
        // channel like a step: written at the stop, applied by the engine the instant it
        // resumes, before the parked instruction runs.
        //
        //  2: :- dynamic(c/1).
        //  3: c(0).
        //  4: run(Out) :-
        //  5:     one(A),
        //  6:     two(B),
        //  7:     Out = pair(A, B).
        //  8: one(A) :- retract(c(A0)), A is A0 + 1, assertz(c(A)).
        //  9: two(20).
        var engine = DebugEngine(
            ":- dynamic(c/1).\nc(0).\n" +
            "run(Out) :-\n    one(A),\n    two(B),\n    Out = pair(A, B).\n" +
            "one(A) :- retract(c(A0)), A is A0 + 1, assertz(c(A)).\ntwo(20).\n");
        engine.AddBreakpoint("<string>", 6);   // the two(B) call — one(A) has run, A = 1

        int stops = 0;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            stops++;
            // FORWARD past two(B) to line 7 (Out = pair): two never runs, B stays free.
            session!.Channel.WriteCommands(
                stops == 1
                    ? new[]
                    {
                        new DebugCommand(DebugCommandKind.SetNextStatement, "<string>", 7),
                        new DebugCommand(DebugCommandKind.Continue),
                    }
                    : new[] { new DebugCommand(DebugCommandKind.Continue) });
        });
        List<Solution> sols;
        using (session)
            sols = engine.QueryAll("run(Out).").ToList();

        Assert.Equal(1, stops);
        Assert.Single(sols);
        // two(B) was skipped: B is still free in the answer, A carries its bound value.
        string outText = sols[0]["Out"]!.ToString()!.Replace(" ", "");
        Assert.Matches(@"^pair\(1,_\w+\)$", outText);
    }
}
