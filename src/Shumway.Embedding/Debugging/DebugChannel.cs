using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Shumway.Embedding.Debugging;

// DebugCommandKind lives in DebugWire.cs, which the debugger side compiles too.

/// <summary>ADR-035 — one command from the debugger. <see cref="File"/>/<see cref="Line"/>
/// carry a breakpoint; <see cref="Condition"/> its goal (empty = unconditional);
/// <see cref="TargetFrame"/> a Set Next Statement's display frame (0 = top).</summary>
public readonly record struct DebugCommand(
    DebugCommandKind Kind, string File = "", int Line = 0, bool Flag = false,
    string Condition = "", int TargetFrame = 0);

/// <summary>
/// ADR-035 — the pinned-memory channel between the engine and the debugger.
///
/// The engine serialises each stop into the snapshot buffer BEFORE tripping the notify
/// breakpoint; the debugger reads it with <c>DkmProcess.ReadMemory</c> and writes
/// commands back with <c>WriteMemory</c>, drained before resume. No code runs in the
/// debuggee while stopped — a func-eval in breakpoint-notification context deadlocks
/// (ConcordExtensibilitySamples #61). Buffers are pinned for the session's lifetime:
/// their addresses are handed out once, at attach.
/// </summary>
public sealed class DebugChannel : IDisposable
{
    // An overflowing snapshot is truncated, never reallocated — the address must not move.
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

        // Born readable and `running`: a debugger attaching mid-flight must be able to
        // read the heartbeat; an all-zero buffer would decode as a dead engine.
        int at = 0;
        DebugWire.WriteInt(_snapshot, ref at, DebugWire.FormatVersion);
        DebugWire.WriteInt(_snapshot, ref at, 0);   // sequence: no stop yet
        DebugWire.WriteInt(_snapshot, ref at, 1);   // running: no stack to show
    }

    /// <summary>Where the debugger reads the current stop from. Stable for the session.</summary>
    public IntPtr SnapshotAddress => _snapshotPin.AddrOfPinnedObject();

    /// <summary>Where the debugger writes its commands. Stable for the session.</summary>
    public IntPtr CommandAddress => _commandsPin.AddrOfPinnedObject();

    /// <summary>Rises on every stop written. A reader compares it before and after to
    /// detect a torn read.</summary>
    public int Sequence { get; private set; }

    // ----- snapshot: engine writes, debugger reads -----

    /// <summary>Serialises a stop into the pinned buffer, before the notify breakpoint
    /// is tripped. Field order is what <see cref="DebugWire.ReadSnapshot"/> expects.</summary>
    public void WriteSnapshot(DebugStopEvent stop) => WriteSnapshot(stop, running: false, interopDepth: 0);

    /// <summary><paramref name="running"/>/<paramref name="interopDepth"/> tell the reader
    /// whether the stack is current: a stop (running false), or a foreign call the engine
    /// published on its way in (running true, depth &gt; 0). See
    /// <see cref="DebugSnapshot.InteropDepth"/>.</summary>
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

        var setNext = stop.SetNextLines;
        DebugWire.WriteInt(_snapshot, ref at, setNext.Count);
        for (int i = 0; i < setNext.Count; i++)
            DebugWire.WriteInt(_snapshot, ref at, setNext[i]);

        // Strings are deduplicated into a table (frames of a recursion share almost all
        // of them); frames carry indices. Each frame is PRICED before any byte of it is
        // written and the counts written are the counts actually present — writing the
        // real count and then running out of room lets the reader walk into the tail of
        // an older, longer stop and read stale bytes as lengths.
        var table = new List<string>();
        var index = new Dictionary<string, int>();
        var accepted = new List<PrologEngine.DebugFrame>();

        // Budget = what remains after the header, minus both counts + terminating zero.
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
            DebugWire.WriteInt(_snapshot, ref at, f.SetNextLines.Count);
            foreach (int l in f.SetNextLines)
                DebugWire.WriteInt(_snapshot, ref at, l);
            DebugWire.WriteInt(_snapshot, ref at, f.Variables.Count);
            foreach (var (name, value) in f.Variables)
            {
                DebugWire.WriteInt(_snapshot, ref at, index[name ?? ""]);
                DebugWire.WriteInt(_snapshot, ref at, index[value ?? ""]);
            }
        }

        // Zero terminator: a reader walking past the end finds an empty count, not the
        // tail of an older stop.
        DebugWire.WriteInt(_snapshot, ref at, 0);
    }

    /// <summary>Cost of adding this frame: fixed fields plus the strings the table does
    /// not hold yet. The new strings are returned un-committed so a rejected frame
    /// leaves none behind.</summary>
    private static int FrameCost(
        PrologEngine.DebugFrame frame, Dictionary<string, int> index, out List<string> newStrings)
    {
        var fresh = new List<string>();
        // nameId arity fileId line pc headArgsId clauseNumber setNextCount varCount
        // + per-set-next-line ints + per-var id pairs.
        int cost = 9 * 4 + frame.SetNextLines.Count * 4 + frame.Variables.Count * 8;

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

    /// <summary>Marks the buffer as the record of a FINISHED stop. Without it, a
    /// process frozen from outside (Break All, a stop in C#) would be shown the Prolog
    /// stack of the last breakpoint as if current. One word, poked at every resume —
    /// keeping a continuously fresh stack instead means rendering the whole environment
    /// chain on a timer, which is prohibitively slow on real programs.</summary>
    public void SetRunning()
    {
        int at = DebugWire.RunningOffset;
        DebugWire.WriteInt(_snapshot, ref at, 1);
    }

    /// <summary>How many foreign calls the engine is inside. Above zero, the stack in
    /// the buffer is the one UNDER the C# the debugger sees (the mixed-stack case);
    /// zero returns the buffer to being a past stop's record.</summary>
    public void SetInteropDepth(int depth)
    {
        int at = DebugWire.InteropDepthOffset;
        DebugWire.WriteInt(_snapshot, ref at, depth);
    }

    private int _heartbeat;

    /// <summary>Bumped as the engine passes goals. A debugger asked to pause reads it
    /// twice: rising means a port (a real stop with a real stack) is microseconds away;
    /// still means no port is coming and freezing the process is the right answer.</summary>
    public void Heartbeat()
    {
        int at = DebugWire.HeartbeatOffset;
        DebugWire.WriteInt(_snapshot, ref at, ++_heartbeat);
    }

    public int HeartbeatValue => _heartbeat;

    /// <summary>Byte copy of the snapshot region, so an Immediate-window evaluation can
    /// restore the stop it interrupted — the returned-to break state's Locals must find
    /// the original frames, not the evaluated goal's.</summary>
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

    /// <summary>Decodes the snapshot with the same code the debugger side uses.</summary>
    public DebugSnapshot? ReadSnapshot() => DebugWire.ReadSnapshot(_snapshot);

    public static DebugSnapshot? ReadSnapshot(byte[] buffer) => DebugWire.ReadSnapshot(buffer);

    // ----- commands: debugger writes, engine reads -----

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
            DebugWire.WriteInt(_commands, ref at, c.TargetFrame);
        }
    }

    /// <summary>ADR-036 — whether the engine has drained the last write (the drain zeroes
    /// the header). The DAP server rewrites its full state on every change and uses this
    /// to retire one-shot commands (a step, a resume) that were already consumed.</summary>
    public bool CommandsConsumed
    {
        get { int at = 0; return DebugWire.ReadInt(_commands, ref at) == 0; }
    }

    /// <summary>Takes everything the debugger left and empties the region — a command is
    /// obeyed once. The engine calls this before resuming.</summary>
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
            int targetFrame = DebugWire.ReadInt(_commands, ref at);
            commands.Add(new DebugCommand(kind, file, line, flag, condition, targetFrame));
        }
        Array.Clear(_commands, 0, 8);   // zero the header: consumed
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
