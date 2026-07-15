using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Shumway.Embedding.Debugging;

/// <summary>
/// ADR-035 — the surface a debugger attaches to, and the session's side of it.
///
/// <para><b>The debugger does not call these methods by these names.</b> It calls
/// <see cref="Shumway.Core.Debugging.ShumwayDebugHost"/>, which forwards here — because a
/// debugger evaluates an expression against a FRAME, and a frame can only name types its
/// own module can see. The frame it stops on is an engine frame, usually the interpreter,
/// and <c>Shumway.Interpreter</c> does not reference <c>Shumway.Embedding</c>. Core is the
/// only assembly all of them share, so that is where the door had to be. The work is
/// here; the handle is there.</para>
///
/// <para><b>Attach</b> is the handshake, and the ONE func-eval the design allows. It runs
/// at attach time, in a normal execution context where evaluating a function in the
/// debuggee is safe and supported — not inside breakpoint notification, where it
/// deadlocks. It hands back the addresses of the pinned channel buffers, and from then on
/// the debugger only reads and writes memory.</para>
///
/// <para>The addresses come back as a STRING rather than a struct or a pointer: it is the
/// one return type that crosses a func-eval with no marshalling assumptions on either
/// side, and this is called once per session, so nothing about it needs to be fast.</para>
/// </summary>
public static class ShumwayDebugHelper
{
    private static DebugChannel? _channel;

    /// <summary>Rises on every stop. See
    /// <see cref="Shumway.Core.Debugging.ShumwayDebugHost.NotifyCount"/>, which is the one
    /// that actually counts — this forwards to it.</summary>
    public static int NotifyCount => Shumway.Core.Debugging.ShumwayDebugHost.NotifyCount;

    /// <summary>The channel <see cref="Attach"/> hands out. Set by the session that owns
    /// it; cleared when the session ends. Setting it wires the Core-level door to this
    /// implementation, and clearing it takes the door away — an attaching debugger is told
    /// there is no session rather than handed a dead address.</summary>
    internal static DebugChannel? Channel
    {
        get => _channel;
        set
        {
            _channel = value;
            PublishChannelFile(value);
            if (value is null)
            {
                Shumway.Core.Debugging.ShumwayDebugHost.OnAttach = null;
                Shumway.Core.Debugging.ShumwayDebugHost.OnCaptureNow = null;
                Shumway.Core.Debugging.ShumwayDebugHost.OnEvaluateGoal = null;
                Shumway.Core.Debugging.ShumwayDebugHost.SnapshotAddress = 0;
                Shumway.Core.Debugging.ShumwayDebugHost.CommandAddress = 0;
                Shumway.Core.Debugging.ShumwayDebugHost.SnapshotLength = 0;
                Shumway.Core.Debugging.ShumwayDebugHost.CommandLength = 0;
                // Last, so a debugger that reads the version first never sees a live version
                // over dead addresses.
                Shumway.Core.Debugging.ShumwayDebugHost.SessionFormatVersion = 0;
            }
            else
            {
                Shumway.Core.Debugging.ShumwayDebugHost.OnAttach = Attach;
                Shumway.Core.Debugging.ShumwayDebugHost.OnCaptureNow = CaptureNow;
                Shumway.Core.Debugging.ShumwayDebugHost.OnEvaluateGoal = EvaluateGoal;
                Shumway.Core.Debugging.ShumwayDebugHost.SnapshotAddress = value.SnapshotAddress.ToInt64();
                Shumway.Core.Debugging.ShumwayDebugHost.SnapshotLength = DebugChannel.SnapshotCapacity;
                Shumway.Core.Debugging.ShumwayDebugHost.CommandAddress = value.CommandAddress.ToInt64();
                Shumway.Core.Debugging.ShumwayDebugHost.CommandLength = DebugChannel.CommandCapacity;
                // Published last: the version is the flag that says the rest is good.
                Shumway.Core.Debugging.ShumwayDebugHost.SessionFormatVersion = DebugChannel.FormatVersion;
            }
        }
    }

    /// <summary>The session <see cref="CaptureNow"/> asks. One per process: there is one
    /// debugger.</summary>
    internal static ChannelDebugSession? Session { get; set; }

    /// <summary>ADR-035 — a stderr line for the <c>--debug-wait</c> entry path. A user who
    /// cannot see into the debugger still sees the terminal the exe runs in, so this is how
    /// they (and we) tell whether the engine armed the entry stop, fired it, and thought a
    /// debugger was attached when it did. On by default under a wait launch (the path already
    /// prints a banner); silence it with <c>SHUMWAY_DEBUG_DIAG=0</c>.</summary>
    public static void DiagLine(string message)
    {
        if (Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_DIAG") == "0") return;
        try { Console.Error.WriteLine("shumway-debug: " + message); } catch (Exception) { }
    }

    /// <summary>ADR-035 — the Immediate window's goal evaluation, plain-text side (the
    /// base64 wrapping lives in <see cref="Shumway.Core.Debugging.ShumwayDebugHost"/>,
    /// where the func-eval lands).</summary>
    public static string EvaluateGoal(int frameIndex, string goalText)
    {
        ChannelDebugSession? session = Session;
        return session is null
            ? "no debug session is running"
            : session.EvaluateGoal(frameIndex, goalText);
    }

    /// <summary>The asynchronous break. The user hit Break All, the process stopped
    /// wherever it happened to be — at no port, so nothing has been reported — and the
    /// channel still holds the last real stop, which would be a lie. This writes the
    /// truth: the stack as it stands right now. Returns the new sequence number, or 0 if
    /// no query is running.
    ///
    /// <para>The SECOND (and last) func-eval the design allows, and it is safe for the
    /// same reason Attach is: a Break All is a normal stop, not the
    /// breakpoint-notification context where evaluating a function deadlocks.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static int CaptureNow()
    {
        ChannelDebugSession? session = Session;
        return session is null ? 0 : session.CaptureNow();
    }

    /// <summary>The stop. The debugger's hidden breakpoint does NOT live here — it lives on
    /// <see cref="Shumway.Core.Debugging.ShumwayDebugHost.Notify"/>, for the module-visibility
    /// reason above. This forwards, so that a caller with only the Embedding surface in hand
    /// still trips it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Notify(int reason) => Shumway.Core.Debugging.ShumwayDebugHost.Notify(reason);

    /// <summary>The handshake. Returns the pinned channel addresses as
    /// <c>"v1;snapshot=&lt;addr&gt;,&lt;len&gt;;commands=&lt;addr&gt;,&lt;len&gt;"</c>,
    /// with addresses in hex, or <c>""</c> if no debug session is running in this
    /// process.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static string Attach()
    {
        var channel = _channel;
        if (channel is null) return "";
        return string.Format(
            CultureInfo.InvariantCulture,
            "v{0};snapshot={1:x},{2};commands={3:x},{4}",
            DebugChannel.FormatVersion,
            channel.SnapshotAddress.ToInt64(),
            DebugChannel.SnapshotCapacity,
            channel.CommandAddress.ToInt64(),
            DebugChannel.CommandCapacity);
    }

    /// <summary>Liveness: proves the debugger can reach the debuggee at all, before
    /// anything depends on it. Returns <see cref="DebugChannel.FormatVersion"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static int Ping() => DebugChannel.FormatVersion;

    /// <summary>The .pl files this process was told to consult, published with the channel.
    /// A breakpoint binds against a module and a module IS a file — and a LAUNCHED process
    /// has stopped nowhere yet, so the debugger has no frames to learn the file names from.
    /// It has to be told, before the first goal runs, or the user's breakpoints have nothing
    /// to attach to. Set before the session opens.</summary>
    public static string[] SourceFiles
    {
        get { lock (_sourceFiles) return _sourceFiles.ToArray(); }
        set
        {
            lock (_sourceFiles)
            {
                _sourceFiles.Clear();
                foreach (string file in value ?? Array.Empty<string>())
                    _sourceFiles.Add(Full(file));
            }
            PublishChannelFile(_channel);
        }
    }

    private static readonly List<string> _sourceFiles = new();

    /// <summary>ADR-035 — "I have just consulted this file." Published to the debugger the
    /// moment it happens, rather than only at session open.
    ///
    /// <para>Which files a program is made of is not settled when it starts: a top level
    /// consults on demand (<c>?- [blint].</c>), and everything the debugger does with a file
    /// — bind a breakpoint, name a frame's language, open it when the user clicks — needs a
    /// module, and a module needs the NAME. Learning it from a stop that has already happened
    /// is one stop too late, which is exactly what the user saw: grey frames on the first
    /// break, real ones on the second.</para></summary>
    public static void NoteSourceFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        string full = Full(path);
        lock (_sourceFiles)
        {
            if (_sourceFiles.Contains(full, StringComparer.OrdinalIgnoreCase)) return;
            _sourceFiles.Add(full);
        }
        PublishChannelFile(_channel);

        // Published is not the same as READ. The debugger can only build the module that
        // stands for a file from inside a stop, so give it one — a stop that shows nothing,
        // resumes at once, and means the user's first break in this file is a break in a file
        // the debugger already knows.
        Session?.SourceFileConsulted();
    }

    private static string Full(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    /// <summary>Where a debugger can find this process's channel WITHOUT running a single
    /// line of its code: <c>%TEMP%\shumway-debug\&lt;pid&gt;.channel</c>.</summary>
    public static string ChannelFilePath(int processId) => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "shumway-debug",
        processId.ToString(CultureInfo.InvariantCulture) + ".channel");

    /// <summary>
    /// ADR-035 D4 — the handshake, in a file.
    ///
    /// <para>Reading a static field of the debuggee is still running code in it, in the sense
    /// that matters here: it needs a THREAD, stopped, in a FRAME whose module can name the
    /// type. A debugger attaching by hand has that (the user pressed Break All). A debugger
    /// LAUNCHING the process does not: nothing ever stops, so there was no moment at which
    /// the channel could be found, so the hidden breakpoint was never armed, so nothing ever
    /// stopped. The program ran to the end and no breakpoint in it could fire.</para>
    ///
    /// <para>So the engine says where its channel is, out loud, the moment the session opens
    /// — before any code is consulted, let alone run. The debugger reads a file. There is no
    /// stop to wait for and nothing to evaluate.</para>
    ///
    /// <para>Best effort by design: a process with no writable temp directory still debugs
    /// by attach, which is where this started.</para>
    /// </summary>
    private static void PublishChannelFile(DebugChannel? channel)
    {
        try
        {
            string path = ChannelFilePath(Environment.ProcessId);
            if (channel is null)
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                return;
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, string.Format(
                CultureInfo.InvariantCulture,
                "v{0};snapshot={1:x},{2};commands={3:x},{4};notify={5};files={6}",
                DebugChannel.FormatVersion,
                channel.SnapshotAddress.ToInt64(),
                DebugChannel.SnapshotCapacity,
                channel.CommandAddress.ToInt64(),
                DebugChannel.CommandCapacity,
                Shumway.Core.Debugging.ShumwayDebugHost.NotifyMetadataToken,
                string.Join("|", SourceFiles)));
        }
        catch (Exception)
        {
            // Attach-by-hand still works; the launch path is what loses.
        }
    }
}
