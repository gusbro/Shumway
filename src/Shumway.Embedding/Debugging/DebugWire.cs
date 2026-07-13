using System;
using System.Collections.Generic;
using System.Text;

namespace Shumway.Embedding.Debugging;

/// <summary>ADR-035 — why execution stopped.</summary>
public enum StopReason
{
    /// <summary>An armed breakpoint was reached.</summary>
    Breakpoint = 0,
    /// <summary>A goal is about to run.</summary>
    Call = 1,
    /// <summary>A goal succeeded.</summary>
    Exit = 2,
    /// <summary>A goal that had succeeded is being retried for another solution.</summary>
    Redo = 3,
    /// <summary>A goal ran out of solutions.</summary>
    Fail = 4,
    /// <summary>Not a port at all: the debugger stopped the process from outside (Break
    /// All) and asked the engine where it was. See
    /// <see cref="ShumwayDebugHelper.CaptureNow"/>.</summary>
    AsyncBreak = 5,
}

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

    /// <summary>"Stop at the next goal, briefly, so I can get my bearings." The debugger's
    /// bootstrap, and the answer to a genuine deadlock: Visual Studio can only create the
    /// objects that represent a .pl file — the ones a breakpoint binds against — from inside
    /// a real stop event; and a stop can only happen once a breakpoint is bound. Neither can
    /// go first. So the debugger asks for a stop it does not need, takes what it needs from
    /// it, and lets the program run on.</summary>
    Hello = 9,
}

/// <summary>ADR-035 — one command, in the form both sides can build. (The engine's own
/// <c>DebugCommand</c> is a record struct, which the debugger's target framework cannot
/// have; this is the shape that crosses.)</summary>
public sealed class DebugWireCommand
{
    public DebugCommandKind Kind { get; set; }
    public string File { get; set; } = "";
    public int Line { get; set; }
    public bool Flag { get; set; }
}

/// <summary>ADR-035 — a stop, as the debugger reads it back out of the channel.</summary>
public sealed class DebugSnapshot
{
    public int Sequence { get; set; }
    public StopReason Reason { get; set; }
    public string Goal { get; set; } = "";
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Depth { get; set; }

    /// <summary>The breakpoint that fired, AS THE USER SET IT — which is not always where
    /// the code turned out to be (a breakpoint on a rule's head binds at its first goal).
    /// A debugger has to match a hit against the line it drew the red dot on, and
    /// <see cref="Line"/> cannot answer that: it says where the machine IS. Empty unless
    /// <see cref="Reason"/> is <see cref="StopReason.Breakpoint"/>.</summary>
    public string BreakFile { get; set; } = "";
    public int BreakLine { get; set; }

    public IReadOnlyList<DebugSnapshotFrame> Frames { get; set; }
        = Array.Empty<DebugSnapshotFrame>();
}

/// <summary>ADR-035 — one frame of a <see cref="DebugSnapshot"/>.</summary>
public sealed class DebugSnapshotFrame
{
    public string Name { get; set; } = "";
    public int Arity { get; set; }
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Pc { get; set; }
    public IReadOnlyList<DebugVariableView> Variables { get; set; }
        = Array.Empty<DebugVariableView>();
}

/// <summary>ADR-035 — a variable of a frame: what the user called it, and what it is
/// bound to right now.</summary>
public sealed class DebugVariableView
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// ADR-035 — the wire format of the debug channel, and the ONE place it is defined.
///
/// <para>This file is compiled into the engine <b>and linked into the Concord
/// components</b> (which target netstandard2.0 and cannot reference the engine at all).
/// That is the point: a debugger and a debuggee that disagree about the layout of a
/// buffer do not fail loudly — they show the user a plausible, wrong stack. Nothing here
/// may use an API the older target framework lacks; the byte-level helpers are written
/// against <c>byte[]</c> and an index for exactly that reason.</para>
///
/// <para>Layout — little-endian ints, length-prefixed UTF-8 strings:</para>
/// <code>
/// snapshot: version, sequence, reason, goal, file, line, depth, breakFile, breakLine,
///           frameCount, { name, arity, file, line, pc,
///                         varCount, { name, value } }
/// commands: version, count, { kind, file, line, flag }
/// </code>
/// </summary>
public static class DebugWire
{
    /// <summary>Bumped whenever the layout changes, so a debugger built against an older
    /// engine says so instead of reading nonsense.</summary>
    public const int FormatVersion = 1;

    // ----- primitives -----

    public static void WriteInt(byte[] buffer, ref int at, int value)
    {
        if (at + 4 > buffer.Length) return;   // truncate: the address must not move
        buffer[at++] = (byte)value;
        buffer[at++] = (byte)(value >> 8);
        buffer[at++] = (byte)(value >> 16);
        buffer[at++] = (byte)(value >> 24);
    }

    public static void WriteString(byte[] buffer, ref int at, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
        if (at + 4 + bytes.Length > buffer.Length)
        {
            WriteInt(buffer, ref at, 0);
            return;
        }
        WriteInt(buffer, ref at, bytes.Length);
        Buffer.BlockCopy(bytes, 0, buffer, at, bytes.Length);
        at += bytes.Length;
    }

    public static int ReadInt(byte[] buffer, ref int at)
    {
        if (at + 4 > buffer.Length) return 0;
        int value = buffer[at]
            | (buffer[at + 1] << 8)
            | (buffer[at + 2] << 16)
            | (buffer[at + 3] << 24);
        at += 4;
        return value;
    }

    public static string ReadString(byte[] buffer, ref int at)
    {
        int length = ReadInt(buffer, ref at);
        if (length <= 0 || at + length > buffer.Length) return "";
        string value = Encoding.UTF8.GetString(buffer, at, length);
        at += length;
        return value;
    }

    // ----- the snapshot -----

    /// <summary>Decodes a snapshot. Returns null if the bytes are not a snapshot this
    /// build understands — an empty buffer, or one written by an engine of a different
    /// format version.</summary>
    public static DebugSnapshot? ReadSnapshot(byte[] buffer)
    {
        if (buffer == null || buffer.Length < 8) return null;

        int at = 0;
        int version = ReadInt(buffer, ref at);
        if (version != FormatVersion) return null;

        var snapshot = new DebugSnapshot
        {
            Sequence = ReadInt(buffer, ref at),
            Reason = (StopReason)ReadInt(buffer, ref at),
            Goal = ReadString(buffer, ref at),
            File = ReadString(buffer, ref at),
            Line = ReadInt(buffer, ref at),
            Depth = ReadInt(buffer, ref at),
            BreakFile = ReadString(buffer, ref at),
            BreakLine = ReadInt(buffer, ref at),
        };

        int frameCount = ReadInt(buffer, ref at);
        var frames = new List<DebugSnapshotFrame>(Math.Max(0, frameCount));
        for (int i = 0; i < frameCount; i++)
        {
            var frame = new DebugSnapshotFrame
            {
                Name = ReadString(buffer, ref at),
                Arity = ReadInt(buffer, ref at),
                File = ReadString(buffer, ref at),
                Line = ReadInt(buffer, ref at),
                Pc = ReadInt(buffer, ref at),
            };
            int varCount = ReadInt(buffer, ref at);
            var variables = new List<DebugVariableView>(Math.Max(0, varCount));
            for (int v = 0; v < varCount; v++)
            {
                variables.Add(new DebugVariableView
                {
                    Name = ReadString(buffer, ref at),
                    Value = ReadString(buffer, ref at),
                });
            }
            frame.Variables = variables;
            frames.Add(frame);
        }
        snapshot.Frames = frames;
        return snapshot;
    }

    // ----- the commands -----

    /// <summary>Encodes the command region. The debugger writes the result with
    /// <c>WriteMemory</c>, in ONE call: the engine drains the region between goals while
    /// it is running (so a breakpoint set on a running process takes effect), and a
    /// command list written in pieces could be read half-formed.
    ///
    /// <para>The debugger writes the WHOLE desired state each time — clear, then every
    /// armed breakpoint — rather than an incremental edit. There is no acknowledgement in
    /// this channel and none is wanted: a full state is idempotent, so it does not matter
    /// whether the engine drained the last one.</para></summary>
    public static byte[] EncodeCommands(IList<DebugWireCommand> commands)
    {
        var buffer = new byte[8192];
        int at = 0;
        WriteInt(buffer, ref at, FormatVersion);
        WriteInt(buffer, ref at, commands == null ? 0 : commands.Count);
        if (commands != null)
        {
            foreach (DebugWireCommand c in commands)
            {
                WriteInt(buffer, ref at, (int)c.Kind);
                WriteString(buffer, ref at, c.File);
                WriteInt(buffer, ref at, c.Line);
                WriteInt(buffer, ref at, c.Flag ? 1 : 0);
            }
        }
        var exact = new byte[at];
        Buffer.BlockCopy(buffer, 0, exact, 0, at);
        return exact;
    }
}
