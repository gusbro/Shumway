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
public partial class Adr035PauseTests
{
    private readonly ITestOutputHelper _log;

    public Adr035PauseTests(ITestOutputHelper log) => _log = log;

    // A program that runs long enough to be paused: a plain counting loop, deep enough
    // that the engine passes far more than one poll interval's worth of goals.
    // (20k debug-mode iterations ≈ hundreds of ms — still orders of magnitude more
    // goals than one poll interval; 200k only made the suite slow.)
    private const string Program = @"
loop(0) :- !.
loop(N) :- N1 is N - 1, loop(N1).
go :- loop(20000).
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
    public void TheTopLevelQueryIsAFrame_BecauseTheUserIsStandingInIt()
    {
        // `?- writeln(uno), debugger_break, writeln(dos).` calls no predicate of the user's:
        // it is builtins and nothing else, so the only thing on the environment chain is the
        // wrapper the engine puts a query in. That wrapper is hidden from error traces —
        // rightly, the user did not write it — and hiding it from the DEBUGGER meant stopping
        // in a query showed an EMPTY STACK. The debugger looked broken; it had simply been
        // told there was nothing there.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- set_prolog_flag(compile_mode, debug).\n"
            + "loop(0) :- !.\n"
            + "loop(N) :- N1 is N - 1, loop(N1).\n");
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

        DebugSnapshot? seen = null;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, notify: _ => seen ??= ReadFromMemory(session!.Channel));

        using (session)
        {
            session.Channel.WriteCommands(new DebugCommand(DebugCommandKind.BreakNow));
            engine.QueryAll("Answer = 42, loop(2000), write(Answer).").ToList();
        }

        Assert.NotNull(seen);
        foreach (var f in seen!.Frames)
            _log.WriteLine($"  {f.Name}/{f.Arity}  [{string.Join(", ", f.Variables.Select(v => v.Name + " = " + v.Value))}]");

        // The query is on the stack, and it is not dressed up as a predicate: arity -1 says
        // "this is not a Name/Arity", so the debugger renders it without one.
        var query = seen.Frames.SingleOrDefault(f => f.Name.StartsWith("?-"));
        Assert.NotNull(query);
        Assert.Equal(-1, query!.Arity);

        // And it says WHICH query. A bare `?-` told the user only that a query was running,
        // which they could see from being stopped in it; the goal they typed is the frame's
        // identity, the way `loop/1` is a clause's.
        Assert.Contains("loop(2000)", query.Name);

        // Its variables are readable too — the wrapper is compiled with a frame map like any
        // other clause. (It gets no BREAK sites: the user cannot set a breakpoint on a line
        // they never wrote.)
        Assert.Contains(query.Variables, v => v.Name == "Answer" && v.Value == "42");
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

    /// <summary>The user's own C#, reachable from Prolog — where a breakpoint in a foreign
    /// predicate lands the debugger.</summary>
    public sealed partial class Scaling
    {
        public static DebugSnapshot? SnapshotSeenFromInsideTheCall;
        public static Func<DebugSnapshot?>? Read;

        [PrologPredicate("scale/2")]
        public static int Scale(int n)
        {
            // Stand exactly where Visual Studio stands when it stops on a breakpoint in here:
            // the engine thread is INSIDE this call and can be asked nothing. Whatever the
            // debugger can see, it has to already be in the buffer.
            SnapshotSeenFromInsideTheCall = Read?.Invoke();
            return n * 2;
        }
    }

    [Fact]
    public void StoppedInsideAForeignPredicate_ThePrologStackUnderTheCSharpIsReadable()
    {
        // The point of an interop debugger: ONE stack, the user's C# over the Prolog that
        // called it. Killing the 50 ms sampler took this away without anyone noticing — the
        // engine only ever published a stack when it STOPPED, and a breakpoint in C# is not a
        // stop of the engine's, so the debugger (rightly) refused to show the last one and
        // the C# stood on nothing. The engine now publishes the stack as it crosses INTO a
        // foreign call, and says it is inside one.
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(Scaling));
        engine.ConsultString(
            ":- set_prolog_flag(compile_mode, debug).\n"
            + "run(In, Out) :- step(In, Out).\n"
            + "step(In, Out) :- scale(In, Out).\n");
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

        using var session = new ChannelDebugSession(engine, notify: _ => { });
        Scaling.Read = () => ReadFromMemory(session.Channel);
        Scaling.SnapshotSeenFromInsideTheCall = null;

        Assert.Equal(16, engine.QueryFirst<int>("run(8, Out).", "Out"));

        var seen = Scaling.SnapshotSeenFromInsideTheCall;
        Assert.NotNull(seen);
        foreach (var f in seen!.Frames) _log.WriteLine($"  {f.Name}/{f.Arity}");

        // RUNNING — the machine has not stopped, and our stepper must not claim a step in
        // there: it is C#, and the CLR steps its own code. But it IS inside a foreign call,
        // and that is what licenses the stack.
        Assert.True(seen.Running);
        Assert.Equal(1, seen.InteropDepth);

        // And the stack is the real one, the goals that led into this C#.
        Assert.Equal(new[] { "step", "run" },
            seen.Frames.Where(f => f.Arity >= 0).Select(f => f.Name).ToArray());
        Assert.Contains(seen.Frames, f => f.Name.StartsWith("?-"));

        // Out of the call, it is history again — nobody may show it at the next unrelated stop.
        DebugSnapshot? after = ReadFromMemory(session.Channel);
        Assert.NotNull(after);
        Assert.Equal(0, after!.InteropDepth);
    }
}
