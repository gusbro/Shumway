using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Shumway.Embedding.Debugging;

// DebugCommandKind lives in DebugWire.cs — the debugger compiles that file too, and it
// is the side that decides what a command is.

/// <summary>ADR-035 — one command from the debugger. <see cref="File"/> /
/// <see cref="Line"/> carry a breakpoint; <see cref="Flag"/> carries a switch.</summary>
public readonly record struct DebugCommand(
    DebugCommandKind Kind, string File = "", int Line = 0, bool Flag = false);

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
    // truncated rather than reallocated, because the address must not move.
    public const int SnapshotCapacity = 256 * 1024;
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
    public void WriteSnapshot(DebugStopEvent stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        int at = 0;
        DebugWire.WriteInt(_snapshot, ref at, DebugWire.FormatVersion);
        DebugWire.WriteInt(_snapshot, ref at, ++Sequence);
        DebugWire.WriteInt(_snapshot, ref at, 0);   // stopped: this stack is where we ARE
        DebugWire.WriteInt(_snapshot, ref at, _heartbeat);
        DebugWire.WriteInt(_snapshot, ref at, (int)stop.Reason);
        DebugWire.WriteString(_snapshot, ref at, stop.Goal);
        DebugWire.WriteString(_snapshot, ref at, stop.File);
        DebugWire.WriteInt(_snapshot, ref at, stop.Line);
        DebugWire.WriteInt(_snapshot, ref at, stop.Depth);
        DebugWire.WriteString(_snapshot, ref at, stop.BreakFile);
        DebugWire.WriteInt(_snapshot, ref at, stop.BreakLine);

        DebugWire.WriteInt(_snapshot, ref at, stop.Frames.Count);
        foreach (var f in stop.Frames)
        {
            DebugWire.WriteString(_snapshot, ref at, f.Name);
            DebugWire.WriteInt(_snapshot, ref at, f.Arity);
            DebugWire.WriteString(_snapshot, ref at, f.File);
            DebugWire.WriteInt(_snapshot, ref at, f.Line);
            DebugWire.WriteInt(_snapshot, ref at, f.Pc);
            DebugWire.WriteInt(_snapshot, ref at, f.Variables.Count);
            foreach (var (name, value) in f.Variables)
            {
                DebugWire.WriteString(_snapshot, ref at, name);
                DebugWire.WriteString(_snapshot, ref at, value);
            }
        }
        // Zero the next word, so a reader that walks off the end of what was just
        // written finds an empty count rather than the tail of an older, longer stop.
        DebugWire.WriteInt(_snapshot, ref at, 0);
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
            commands.Add(new DebugCommand(kind, file, line, flag));
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
