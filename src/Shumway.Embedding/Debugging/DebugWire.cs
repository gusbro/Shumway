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

/// <summary>ADR-035 — a stop, as the debugger reads it back out of the channel.</summary>
public sealed class DebugSnapshot
{
    public int Sequence { get; set; }
    public StopReason Reason { get; set; }
    public string Goal { get; set; } = "";
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Depth { get; set; }
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
/// snapshot: version, sequence, reason, goal, file, line, depth,
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
}
