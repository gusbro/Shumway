using System;
using System.Globalization;
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
            if (value is null)
            {
                Shumway.Core.Debugging.ShumwayDebugHost.OnAttach = null;
                Shumway.Core.Debugging.ShumwayDebugHost.OnCaptureNow = null;
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
}
