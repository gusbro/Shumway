using System;
using System.Linq;
using System.Runtime.InteropServices;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 — the pause, and the reason a debugger may never show a stack that is not the
/// program's.
///
/// <para>The engine used to keep a rendered stack lying around at all times, refreshed on a
/// 50 ms clock, so that a Break All would find something to display. It was wrong twice
/// over. It was a LIE — what it displayed was where the program had been up to 50 ms ago,
/// not where it was. And it was ruinous: a refresh walks the whole environment chain and
/// renders every variable of every frame, and under a debugger (last-call optimisation off)
/// that chain is as deep as the recursion. A real program could not finish.</para>
///
/// <para>What replaced it is what an interpreter's debugger is supposed to do: a pause is a
/// REQUEST — keep running, briefly, and stop at the next port, where the stack means
/// something. These tests pin all three halves of that: the request lands at a real port;
/// the engine says so when a stack is history rather than current; and it never renders one
/// unless somebody stopped.</para>
/// </summary>
public class Adr035PauseTests
{
    private readonly ITestOutputHelper _log;

    public Adr035PauseTests(ITestOutputHelper log) => _log = log;

    // A program that runs long enough to be paused: a plain counting loop, deep enough
    // that the engine passes far more than one poll interval's worth of goals.
    private const string Program = @"
loop(0) :- !.
loop(N) :- N1 is N - 1, loop(N1).
go :- loop(200000).
";

    private static PrologEngine DebugEngine()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + Program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    /// <summary>Reads the snapshot the way a debugger does: from the raw address.</summary>
    private static DebugSnapshot? ReadFromMemory(DebugChannel channel)
    {
        var bytes = new byte[DebugChannel.SnapshotCapacity];
        Marshal.Copy(channel.SnapshotAddress, bytes, 0, bytes.Length);
        return DebugChannel.ReadSnapshot(bytes);
    }

    [Fact]
    public void APause_StopsAtTheNextPort_WithARealStack()
    {
        var engine = DebugEngine();

        DebugSnapshot? seen = null;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ =>
        {
            seen ??= ReadFromMemory(session!.Channel);
        });

        using (session)
        {
            // The debugger asks to pause — exactly as Concord does, by writing the command
            // into the pinned region of a program that is RUNNING. (Here we write it before
            // the query starts, which is the same thing from the engine's side: it reads the
            // channel between goals, and takes the first poll that sees it.)
            session.Channel.WriteCommands(new DebugCommand(DebugCommandKind.BreakNow));
            engine.QueryAll("go.").ToList();
        }

        Assert.NotNull(seen);
        _log.WriteLine($"{seen!.Reason} at {seen.File}:{seen.Line}, depth {seen.Depth}");
        foreach (var f in seen.Frames)
            _log.WriteLine($"  {f.Name}/{f.Arity} {f.File}:{f.Line}");

        // A real stop, at a real port, in the program the user is running — not a
        // reconstruction of where it was a moment ago.
        Assert.Equal(StopReason.AsyncBreak, seen.Reason);
        Assert.False(seen.Running);
        Assert.NotEmpty(seen.Frames);
        Assert.Contains(seen.Frames, f => f.Name == "loop" || f.Name == "go");
    }

    [Fact]
    public void WhileTheProgramRuns_TheSnapshotSaysSo_SoNobodyShowsAStackThatIsHistory()
    {
        var engine = DebugEngine();

        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ => { });

        using (session)
        {
            session.Channel.WriteCommands(new DebugCommand(DebugCommandKind.BreakNow));
            engine.QueryAll("go.").ToList();

            // The stop happened, and then the program ran on. What is in the buffer is the
            // record of a stop that is OVER. A debugger that freezes the process now — a raw
            // Break All, a breakpoint in the user's C# — must be told that, or it will paint
            // these frames on the screen as though the program were standing in them.
            DebugSnapshot? after = ReadFromMemory(session.Channel);
            Assert.NotNull(after);
            Assert.True(after!.Running);
        }
    }

    [Fact]
    public void ABreakpointSetOnAnIdleEngine_BeforeAnyQueryHasEverRun_StillHits()
    {
        // The ordinary way to debug a program you did not launch from the IDE: the engine
        // consults it and WAITS at the prompt, you attach, and you set a breakpoint on a
        // predicate nothing has called yet. Not one goal has run in this engine's life.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- set_prolog_flag(compile_mode, debug).\n"
            + "go :- step(1, A), step(A, B), B > 0.\n"
            + "step(N, Out) :- Out is N * 2.\n");

        int bound = engine.AddBreakpoint("<string>", 3);   // the body of step/2
        Assert.True(bound > 0, "the breakpoint bound nothing at all");

        DebugSnapshot? seen = null;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ => seen ??= ReadFromMemory(session!.Channel));

        using (session)
            Assert.True(engine.QueryAll("go.").Any());

        Assert.NotNull(seen);
        _log.WriteLine($"{seen!.Reason} at {seen.File}:{seen.Line}");
        Assert.Equal(StopReason.Breakpoint, seen.Reason);
        Assert.Contains(seen.Frames, f => f.Name == "step");
    }

    [Fact]
    public void DebuggerBreak_WithNobodyWatching_IsANoOp()
    {
        // The whole value of debugger_break/0 is that you can LEAVE it in the program. A
        // build with no debugger attached must run straight through it — no stop, no stack
        // rendered, no cost. (With a debugger attached it calls Debugger.Break(), which is a
        // thing only a debugger can answer; there is no way to assert that from a test
        // process that is not being debugged, and pretending otherwise would test the mock.)
        var engine = DebugEngine();
        engine.ConsultString("guarded :- debugger_break, true.\n");

        int notifies = 0;
        using var session = new ChannelDebugSession(engine, notify: _ => notifies++);

        Assert.True(engine.QueryAll("guarded.").Any());
        Assert.Equal(0, notifies);
    }

    [Fact]
    public void AProgramNobodyPaused_IsNeverAskedForItsStack()
    {
        var engine = DebugEngine();

        int notifies = 0;
        using var session = new ChannelDebugSession(engine, notify: _ => notifies++);

        engine.QueryAll("go.").ToList();

        // 200,000 goals, no breakpoint, no step, no pause: the engine must not have stopped
        // once, nor rendered a single stack. (This is the whole regression: it used to
        // render one every 50 ms — walking an environment chain 200,000 frames deep — and a
        // program of any size never finished.)
        Assert.Equal(0, notifies);

        // The heartbeat, on the other hand, does move: it is one word, and it is what lets a
        // debugger tell a running Prolog machine (pause it at its next port) from one that
        // will never reach another port at all.
        DebugSnapshot? snapshot = ReadFromMemory(session.Channel);
        Assert.NotNull(snapshot);
        _log.WriteLine($"heartbeat after 200k goals: {snapshot!.Heartbeat}");
        Assert.True(snapshot.Heartbeat > 0);
    }
}
