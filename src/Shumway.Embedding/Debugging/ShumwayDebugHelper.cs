using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Shumway.Embedding.Debugging;

/// <summary>
/// ADR-035 — the two-method surface a debugger attaches to.
///
/// <para><b>Notify</b> is the stop. The debugger plants a hidden breakpoint on it (a
/// CLR instruction breakpoint, invisible to the user), and the engine calls it once the
/// snapshot is already written. So the sequence at every stop is: serialise, call
/// Notify, get stopped by the runtime, be resumed with commands waiting in the channel.
/// It is deliberately the dullest method in the codebase — no arguments to marshal, no
/// work to do, nothing that could fail — because everything about it that matters
/// happens in the debugger, not here.</para>
///
/// <para><b>Attach</b> is the handshake, and the ONE func-eval the design allows. It
/// runs at attach time, in a normal execution context where evaluating a function in
/// the debuggee is safe and supported — not inside breakpoint notification, where it
/// deadlocks. It hands back the addresses of the pinned channel buffers, and from then
/// on the debugger only reads and writes memory.</para>
///
/// <para>The addresses come back as a STRING rather than a struct or a pointer: it is
/// the one return type that crosses a func-eval with no marshalling assumptions on
/// either side, and this is called once per session, so nothing about it needs to be
/// fast.</para>
/// </summary>
public static class ShumwayDebugHelper
{
    private static DebugChannel? _channel;

    /// <summary>Rises on every stop. A debugger that missed a notification can tell.
    /// Also keeps <see cref="Notify"/> from being optimised into nothing.</summary>
    public static volatile int NotifyCount;

    /// <summary>The channel <see cref="Attach"/> hands out. Set by the session that owns
    /// it; cleared when the session ends.</summary>
    internal static DebugChannel? Channel
    {
        get => _channel;
        set => _channel = value;
    }

    /// <summary>The stop. Called by the engine with the snapshot already written; the
    /// debugger's hidden breakpoint lives on this method.
    ///
    /// <para>NoInlining because a breakpoint needs a method to be planted on, and
    /// NoOptimization because a method that does nothing observable is a method the JIT
    /// is entitled to make disappear.</para></summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static void Notify(int reason)
    {
        NotifyCount++;
        // The debugger stops the process here. Nothing else belongs in this method:
        // whatever it needs is in the channel, and it reads that with ReadMemory.
        GC.KeepAlive(_channel);
    }

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
