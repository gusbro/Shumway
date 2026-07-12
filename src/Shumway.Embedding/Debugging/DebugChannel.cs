using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Shumway.Embedding.Debugging;

/// <summary>ADR-035 — what the debugger asked the engine to do next.</summary>
public enum DebugCommandKind
{
    None = 0,
    Continue = 1,
    StepInto = 2,
    StepOver = 3,
    StepOut = 4,
    AddBreakpoint = 5,
    RemoveBreakpoint = 6,
    ClearBreakpoints = 7,
    SetLastCallOptimisation = 8,
}

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
/// somewhere else.</para>
/// </summary>
public sealed class DebugChannel : IDisposable
{
    // Big enough for a deep stack with variables; a snapshot that would overflow is
    // truncated rather than reallocated, because the address must not move.
    public const int SnapshotCapacity = 256 * 1024;
    public const int CommandCapacity = 16 * 1024;

    /// <summary>Bumped whenever the layout below changes, so a debugger built against
    /// an older engine says so instead of reading nonsense.</summary>
    public const int FormatVersion = 1;

    private readonly byte[] _snapshot = new byte[SnapshotCapacity];
    private readonly byte[] _commands = new byte[CommandCapacity];
    private GCHandle _snapshotPin;
    private GCHandle _commandsPin;
    private bool _disposed;

    public DebugChannel()
    {
        _snapshotPin = GCHandle.Alloc(_snapshot, GCHandleType.Pinned);
        _commandsPin = GCHandle.Alloc(_commands, GCHandleType.Pinned);
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
    /// needs is already there and nothing has to run to produce it.</summary>
    public void WriteSnapshot(DebugStopEvent stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        var w = new Writer(_snapshot);
        w.Int(FormatVersion);
        w.Int(++Sequence);
        w.Int((int)stop.Reason);
        w.Str(stop.Goal);
        w.Str(stop.File);
        w.Int(stop.Line);
        w.Int(stop.Depth);

        w.Int(stop.Frames.Count);
        foreach (var f in stop.Frames)
        {
            w.Str(f.Name);
            w.Int(f.Arity);
            w.Str(f.File);
            w.Int(f.Line);
            w.Int(f.Pc);
            w.Int(f.Variables.Count);
            foreach (var (name, value) in f.Variables)
            {
                w.Str(name);
                w.Str(value);
            }
        }
        w.Terminate();
    }

    /// <summary>Reads a snapshot back. The debugger does this across process boundaries
    /// with ReadMemory; in-process this is the same decode, and is what the tests use to
    /// prove the two agree.</summary>
    public static DebugSnapshot ReadSnapshot(ReadOnlySpan<byte> buffer)
    {
        var r = new Reader(buffer);
        int version = r.Int();
        if (version != FormatVersion)
            throw new InvalidOperationException(
                $"Debug channel format {version}; this debugger speaks {FormatVersion}.");

        int sequence = r.Int();
        var reason = (StopReason)r.Int();
        string goal = r.Str();
        string file = r.Str();
        int line = r.Int();
        int depth = r.Int();

        int frameCount = r.Int();
        var frames = new List<DebugSnapshotFrame>(frameCount);
        for (int i = 0; i < frameCount; i++)
        {
            string name = r.Str();
            int arity = r.Int();
            string frameFile = r.Str();
            int frameLine = r.Int();
            int pc = r.Int();
            int varCount = r.Int();
            var vars = new List<(string, string)>(varCount);
            for (int v = 0; v < varCount; v++)
                vars.Add((r.Str(), r.Str()));
            frames.Add(new DebugSnapshotFrame(name, arity, frameFile, frameLine, pc, vars));
        }
        return new DebugSnapshot(sequence, reason, goal, file, line, depth, frames);
    }

    /// <summary>The snapshot as it stands in the pinned buffer.</summary>
    public DebugSnapshot ReadSnapshot() => ReadSnapshot(_snapshot);

    // ----- commands: debugger writes, engine reads -----

    /// <summary>Writes commands into the pinned region. The debugger does this with
    /// WriteMemory; the tests do it directly, over the same bytes.</summary>
    public void WriteCommands(params DebugCommand[] commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var w = new Writer(_commands);
        w.Int(FormatVersion);
        w.Int(commands.Length);
        foreach (var c in commands)
        {
            w.Int((int)c.Kind);
            w.Str(c.File);
            w.Int(c.Line);
            w.Int(c.Flag ? 1 : 0);
        }
        w.Terminate();
    }

    /// <summary>Takes everything the debugger left, and empties the region — a command
    /// is obeyed once. The engine does this before it resumes.</summary>
    public IReadOnlyList<DebugCommand> DrainCommands()
    {
        var r = new Reader(_commands);
        if (_commands[0] == 0 && _commands[1] == 0 && _commands[2] == 0 && _commands[3] == 0)
            return Array.Empty<DebugCommand>();

        int version = r.Int();
        if (version != FormatVersion) return Array.Empty<DebugCommand>();

        int count = r.Int();
        var commands = new List<DebugCommand>(count);
        for (int i = 0; i < count; i++)
        {
            var kind = (DebugCommandKind)r.Int();
            string file = r.Str();
            int line = r.Int();
            bool flag = r.Int() != 0;
            commands.Add(new DebugCommand(kind, file, line, flag));
        }
        Array.Clear(_commands, 0, 8);   // consumed
        return commands;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_snapshotPin.IsAllocated) _snapshotPin.Free();
        if (_commandsPin.IsAllocated) _commandsPin.Free();
    }

    // ----- the wire format: little-endian ints, length-prefixed UTF-8 -----

    private ref struct Writer
    {
        private readonly Span<byte> _buffer;
        private int _at;

        public Writer(Span<byte> buffer)
        {
            _buffer = buffer;
            _at = 0;
        }

        public void Int(int value)
        {
            if (_at + 4 > _buffer.Length) return;   // truncate: the address must not move
            _buffer[_at++] = (byte)value;
            _buffer[_at++] = (byte)(value >> 8);
            _buffer[_at++] = (byte)(value >> 16);
            _buffer[_at++] = (byte)(value >> 24);
        }

        public void Str(string value)
        {
            int bytes = Encoding.UTF8.GetByteCount(value);
            if (_at + 4 + bytes > _buffer.Length) { Int(0); return; }
            Int(bytes);
            Encoding.UTF8.GetBytes(value, _buffer.Slice(_at, bytes));
            _at += bytes;
        }

        /// <summary>Zeroes the next word, so a reader that walks off the end of what was
        /// written finds an empty count rather than the tail of an older, longer
        /// snapshot.</summary>
        public void Terminate() => Int(0);
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private int _at;

        public Reader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _at = 0;
        }

        public int Int()
        {
            if (_at + 4 > _buffer.Length) return 0;
            int value = _buffer[_at]
                | (_buffer[_at + 1] << 8)
                | (_buffer[_at + 2] << 16)
                | (_buffer[_at + 3] << 24);
            _at += 4;
            return value;
        }

        public string Str()
        {
            int length = Int();
            if (length <= 0 || _at + length > _buffer.Length) return "";
            string value = Encoding.UTF8.GetString(_buffer.Slice(_at, length));
            _at += length;
            return value;
        }
    }
}

/// <summary>ADR-035 — a stop, as the debugger reads it back out of the channel.</summary>
public sealed record DebugSnapshot(
    int Sequence,
    StopReason Reason,
    string Goal,
    string File,
    int Line,
    int Depth,
    IReadOnlyList<DebugSnapshotFrame> Frames);

/// <summary>ADR-035 — one frame of a <see cref="DebugSnapshot"/>.</summary>
public sealed record DebugSnapshotFrame(
    string Name,
    int Arity,
    string File,
    int Line,
    int Pc,
    IReadOnlyList<(string Name, string Value)> Variables);
