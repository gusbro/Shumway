using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Shumway.Embedding.Debugging;

// DebugCommandKind lives in DebugWire.cs — the debugger compiles that file too, and it
// is the side that decides what a command is.

/// <summary>ADR-035 — one command from the debugger. <see cref="File"/> /
/// <see cref="Line"/> carry a breakpoint; <see cref="Flag"/> carries a switch;
/// <see cref="Condition"/> carries a conditional breakpoint's goal (empty =
/// unconditional).</summary>
public readonly record struct DebugCommand(
    DebugCommandKind Kind, string File = "", int Line = 0, bool Flag = false,
    string Condition = "");

/// <summary>
/// ADR-035 — the pinned-memory channel between the engine and the debugger.
///
/// <para><b>Why memory and not a func-eval.</b> A debugger that is stopped can ask the
/// debuggee to run a method (a "func-eval"), and that is the obvious way to ask the
/// engine what its stack looks like. It is also the way to hang: evaluating a function
/// inside breakpoint-notification context is documented to deadlock
/// (ConcordExtensibilitySamples #61), and the D0 spike confirmed the hazard is real.
/// So the engine does the work FIRST — it serialises the whole stop into a buffer whose
/// address never moves — and only then trips the breakpoint. The debugger reads that
/// memory (<c>DkmProcess.ReadMemory</c>), which needs nothing from the debuggee at all,
/// and writes its answer back into the command region (<c>WriteMemory</c>). The engine
/// drains it before resuming. No code runs in the debuggee while it is stopped.</para>
///
/// <para>The buffers are pinned for the life of the session: the addresses are handed
/// to the debugger once, at attach, and a moving GC would otherwise leave it reading
/// somewhere else. The format lives in <see cref="DebugWire"/>, which the debugger
/// compiles too — one definition, so the two cannot disagree.</para>
/// </summary>
public sealed class DebugChannel : IDisposable
{
    // Big enough for a deep stack with variables; a snapshot that would overflow is
    // truncated rather than reallocated, because the address must not move. The size lives in
    // DebugWire, with the format: the debugger reads the region whole, so both sides need it.
    public const int SnapshotCapacity = DebugWire.SnapshotCapacity;
    public const int CommandCapacity = 16 * 1024;

    /// <summary>The wire format both sides speak. See <see cref="DebugWire"/>.</summary>
    public const int FormatVersion = DebugWire.FormatVersion;

    private readonly byte[] _snapshot = new byte[SnapshotCapacity];
    private readonly byte[] _commands = new byte[CommandCapacity];
    private GCHandle _snapshotPin;
    private GCHandle _commandsPin;
    private bool _disposed;

    public DebugChannel()
    {
        _snapshotPin = GCHandle.Alloc(_snapshot, GCHandleType.Pinned);
        _commandsPin = GCHandle.Alloc(_commands, GCHandleType.Pinned);

        // A channel is born READABLE and RUNNING, before anything has ever stopped. A
        // debugger that attaches to a program already in flight has to be able to read the
        // heartbeat — that is how it learns Prolog is moving, and therefore that a pause can
        // be answered at a port. An all-zero buffer decodes as nothing at all, and it would
        // conclude the engine was dead. (The rest of the fields stay zero: an empty stop, and
        // `running` says it is not a stop at all.)
        int at = 0;
        DebugWire.WriteInt(_snapshot, ref at, DebugWire.FormatVersion);
        DebugWire.WriteInt(_snapshot, ref at, 0);   // no stop has happened yet
        DebugWire.WriteInt(_snapshot, ref at, 1);   // running: there is no stack to show
    }

    /// <summary>Where the debugger reads the current stop from. Stable for the life of
    /// the session.</summary>
    public IntPtr SnapshotAddress => _snapshotPin.AddrOfPinnedObject();

    /// <summary>Where the debugger writes its commands. Stable for the life of the
    /// session.</summary>
    public IntPtr CommandAddress => _commandsPin.AddrOfPinnedObject();

    /// <summary>Rises by one on every stop written. A debugger reads it first and last:
    /// if it changed underneath, the snapshot it just read was torn and it reads
    /// again.</summary>
    public int Sequence { get; private set; }

    // ----- snapshot: engine writes, debugger reads -----

    /// <summary>Serialises a stop into the pinned buffer. Called BEFORE the notify
    /// breakpoint is tripped, so that by the time the debugger is looking, everything it
    /// needs is already there and nothing has to run to produce it. Field order is the
    /// one <see cref="DebugWire.ReadSnapshot"/> expects.</summary>
    public void WriteSnapshot(DebugStopEvent stop) => WriteSnapshot(stop, running: false, interopDepth: 0);

    /// <summary>The general form. <paramref name="running"/> and
    /// <paramref name="interopDepth"/> are what a reader consults BEFORE the stack, to know
    /// whether it is the stack the program is standing in: a stop (running false), or a
    /// foreign call the engine is blocked inside and published on its way into (running true,
    /// depth above zero). See <see cref="DebugSnapshot.InteropDepth"/>.</summary>
    public void WriteSnapshot(DebugStopEvent stop, bool running, int interopDepth)
    {
        ArgumentNullException.ThrowIfNull(stop);
        int at = 0;
        DebugWire.WriteInt(_snapshot, ref at, DebugWire.FormatVersion);
        DebugWire.WriteInt(_snapshot, ref at, ++Sequence);
        DebugWire.WriteInt(_snapshot, ref at, running ? 1 : 0);
        DebugWire.WriteInt(_snapshot, ref at, _heartbeat);
        DebugWire.WriteInt(_snapshot, ref at, interopDepth);
        DebugWire.WriteInt(_snapshot, ref at, (int)stop.Reason);
        DebugWire.WriteString(_snapshot, ref at, stop.Goal);
        DebugWire.WriteString(_snapshot, ref at, stop.File);
        DebugWire.WriteInt(_snapshot, ref at, stop.Line);
        DebugWire.WriteInt(_snapshot, ref at, stop.Depth);
        DebugWire.WriteString(_snapshot, ref at, stop.BreakFile);
        DebugWire.WriteInt(_snapshot, ref at, stop.BreakLine);
        DebugWire.WriteString(_snapshot, ref at, stop.ConditionError);

        // ADR-035 D5+ — the Set Next Statement valid lines (small: a clause's statements).
        var setNext = stop.SetNextLines;
        DebugWire.WriteInt(_snapshot, ref at, setNext.Count);
        for (int i = 0; i < setNext.Count; i++)
            DebugWire.WriteInt(_snapshot, ref at, setNext[i]);

        // EVERY STRING ONCE. A stack's frames mostly repeat each other: the same file on
        // every frame, the same predicate down a recursion, the same variable names level
        // after level — and, above all, the same VALUES, because a call stack is mostly the
        // same bindings seen from different clauses (the engine's per-capture bag hands the
        // same string instance to every frame that shares a term). So the snapshot carries a
        // string TABLE, and the frames carry indices into it. A 2 700-frame recursion whose
        // frames share their data serialises the data once, not 2 700 times.
        //
        // A STACK THAT DOES NOT FIT IS STILL TRUNCATED, AND STILL SAYS SO. Each frame is
        // priced before a byte of it is written — its fixed fields plus the strings the table
        // does not already hold — and the counts written are the counts actually in the
        // buffer. (Writing the real count and then running out of room was the bug that hung
        // Break All on Blint: the reader walked missing frames through the tail of an OLDER
        // stop, read old bytes as a length, and died of an OutOfMemoryException inside the
        // one event that could have completed the pause.)
        var table = new List<string>();
        var index = new Dictionary<string, int>();
        var accepted = new List<PrologEngine.DebugFrame>();

        // The header is already written; everything after `at` must fit in what remains,
        // including both counts and the terminating zero word.
        int budget = SnapshotCapacity - at - 12;
        foreach (var f in stop.Frames)
        {
            int cost = FrameCost(f, index, out var newStrings);
            if (cost > budget) break;
            budget -= cost;
            foreach (string s in newStrings)
            {
                index[s] = table.Count;
                table.Add(s);
            }
            accepted.Add(f);
        }

        DebugWire.WriteInt(_snapshot, ref at, table.Count);
        foreach (string s in table)
            DebugWire.WriteString(_snapshot, ref at, s);

        DebugWire.WriteInt(_snapshot, ref at, accepted.Count);
        foreach (var f in accepted)
        {
            DebugWire.WriteInt(_snapshot, ref at, index[f.Name ?? ""]);
            DebugWire.WriteInt(_snapshot, ref at, f.Arity);
            DebugWire.WriteInt(_snapshot, ref at, index[f.File ?? ""]);
            DebugWire.WriteInt(_snapshot, ref at, f.Line);
            DebugWire.WriteInt(_snapshot, ref at, f.Pc);
            DebugWire.WriteInt(_snapshot, ref at, index[f.HeadArgs ?? ""]);
            DebugWire.WriteInt(_snapshot, ref at, f.ClauseNumber);
            DebugWire.WriteInt(_snapshot, ref at, f.Variables.Count);
            foreach (var (name, value) in f.Variables)
            {
                DebugWire.WriteInt(_snapshot, ref at, index[name ?? ""]);
                DebugWire.WriteInt(_snapshot, ref at, index[value ?? ""]);
            }
        }

        // Zero the next word, so a reader that walks off the end of what was just
        // written finds an empty count rather than the tail of an older, longer stop.
        DebugWire.WriteInt(_snapshot, ref at, 0);
    }

    /// <summary>What adding this frame costs: its fixed fields, plus the strings the table
    /// does not hold yet — returned so the caller can commit them only if the frame is
    /// accepted. A frame rejected for size must leave no strings behind.</summary>
    private static int FrameCost(
        PrologEngine.DebugFrame frame, Dictionary<string, int> index, out List<string> newStrings)
    {
        var fresh = new List<string>();
        // nameId arity fileId line pc headArgsId clauseNumber varCount + per-var ids.
        int cost = 8 * 4 + frame.Variables.Count * 8;

        int StringCost(string? s)
        {
            s ??= "";
            if (index.ContainsKey(s) || fresh.Contains(s)) return 0;
            fresh.Add(s);
            return 4 + System.Text.Encoding.UTF8.GetByteCount(s);
        }

        cost += StringCost(frame.Name);
        cost += StringCost(frame.File);
        cost += StringCost(frame.HeadArgs);
        foreach (var (name, value) in frame.Variables)
        {
            cost += StringCost(name);
            cost += StringCost(value);
        }
        newStrings = fresh;
        return cost;
    }

    /// <summary>Says that the program is running again, so that what the buffer holds is
    /// the record of a stop that is OVER.
    ///
    /// <para>A debugger that freezes the process from outside — the CLR's own Break All, or
    /// any stop in C# or native code — reads this buffer and would otherwise dress the
    /// screen with the Prolog stack of the last breakpoint, which the program is no longer
    /// anywhere near. One word, poked in place at every resume: the alternative (keeping a
    /// fresh stack lying around at all times) means rendering the entire environment chain
    /// on a timer, which is what made a real program under the debugger never finish.</para>
    /// </summary>
    public void SetRunning()
    {
        int at = DebugWire.RunningOffset;
        DebugWire.WriteInt(_snapshot, ref at, 1);
    }

    /// <summary>"The engine is inside this many foreign calls." Zero puts the buffer back to
    /// being the record of a past stop; above zero says the stack in it is the one under the
    /// C# the debugger is about to be looking at. One word, in place — this is crossed at
    /// every foreign call.</summary>
    public void SetInteropDepth(int depth)
    {
        int at = DebugWire.InteropDepthOffset;
        DebugWire.WriteInt(_snapshot, ref at, depth);
    }

    private int _heartbeat;

    /// <summary>"Prolog is still moving." Bumped as the engine passes goals — one word, in
    /// place. A debugger asked to pause reads it twice: if it is rising, the engine will
    /// reach a port in microseconds and the pause can be honoured there, with a real stack;
    /// if it is still, no port is ever coming (a blocked read, a finished query, a long
    /// spell in C#) and the debugger is right to freeze the process instead.</summary>
    public void Heartbeat()
    {
        int at = DebugWire.HeartbeatOffset;
        DebugWire.WriteInt(_snapshot, ref at, ++_heartbeat);
    }

    /// <summary>The heartbeat as it stands. The engine's own idle watcher reads it for the
    /// same reason the debugger does: to tell a machine that is passing goals from one that
    /// is standing still.</summary>
    public int HeartbeatValue => _heartbeat;

    /// <summary>ADR-035 — the snapshot region, byte for byte, so an Immediate-window
    /// evaluation can put back the stop it interrupted. The eval's own stops overwrite
    /// the buffer; when it finishes, Visual Studio returns the user to the ORIGINAL break
    /// state, whose Locals still read from this buffer — and they must find the frames
    /// they were reading, not the evaluated goal's.</summary>
    public byte[] SaveSnapshotBytes()
    {
        var saved = new byte[SnapshotCapacity];
        Buffer.BlockCopy(_snapshot, 0, saved, 0, SnapshotCapacity);
        return saved;
    }

    public void RestoreSnapshotBytes(byte[] saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        Buffer.BlockCopy(saved, 0, _snapshot, 0, Math.Min(saved.Length, SnapshotCapacity));
    }

    /// <summary>The snapshot as it stands in the pinned buffer, decoded with the very
    /// code the debugger uses.</summary>
    public DebugSnapshot? ReadSnapshot() => DebugWire.ReadSnapshot(_snapshot);

    /// <summary>Decodes bytes read from the pinned buffer — the debugger's path, and
    /// what the tests use to prove the two agree.</summary>
    public static DebugSnapshot? ReadSnapshot(byte[] buffer) => DebugWire.ReadSnapshot(buffer);

    // ----- commands: debugger writes, engine reads -----

    /// <summary>Writes commands into the pinned region. The debugger does this with
    /// WriteMemory; the tests do it directly, over the same bytes.</summary>
    public void WriteCommands(params DebugCommand[] commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        int at = 0;
        DebugWire.WriteInt(_commands, ref at, DebugWire.FormatVersion);
        DebugWire.WriteInt(_commands, ref at, commands.Length);
        foreach (var c in commands)
        {
            DebugWire.WriteInt(_commands, ref at, (int)c.Kind);
            DebugWire.WriteString(_commands, ref at, c.File);
            DebugWire.WriteInt(_commands, ref at, c.Line);
            DebugWire.WriteInt(_commands, ref at, c.Flag ? 1 : 0);
            DebugWire.WriteString(_commands, ref at, c.Condition);
        }
    }

    /// <summary>Takes everything the debugger left, and empties the region — a command
    /// is obeyed once. The engine does this before it resumes.</summary>
    public IReadOnlyList<DebugCommand> DrainCommands()
    {
        int at = 0;
        int version = DebugWire.ReadInt(_commands, ref at);
        if (version != DebugWire.FormatVersion) return Array.Empty<DebugCommand>();

        int count = DebugWire.ReadInt(_commands, ref at);
        var commands = new List<DebugCommand>(Math.Max(0, count));
        for (int i = 0; i < count; i++)
        {
            var kind = (DebugCommandKind)DebugWire.ReadInt(_commands, ref at);
            string file = DebugWire.ReadString(_commands, ref at);
            int line = DebugWire.ReadInt(_commands, ref at);
            bool flag = DebugWire.ReadInt(_commands, ref at) != 0;
            string condition = DebugWire.ReadString(_commands, ref at);
            commands.Add(new DebugCommand(kind, file, line, flag, condition));
        }
        Array.Clear(_commands, 0, 8);   // consumed: a step asked for once is taken once
        return commands;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_snapshotPin.IsAllocated) _snapshotPin.Free();
        if (_commandsPin.IsAllocated) _commandsPin.Free();
    }
}
