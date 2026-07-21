using System;
using System.Collections.Generic;

namespace Shumway.Embedding.Debugging;

/// <summary>
/// ADR-035 — how <see cref="PrologEngine.EnableDebugging(DebugOptions)"/> opens a debug
/// session. Every field has the default the REPL's <c>--debug</c> uses, so the common case
/// is <c>engine.EnableDebugging()</c> with no options at all.
///
/// <para>The point of the method is to make Shumway debuggable when it is EMBEDDED — a
/// Prolog engine that is one part of a larger .NET application, in the application's own
/// process, rather than the standalone REPL. A debugger attached to that process then sets
/// breakpoints in the <c>.pl</c> files the engine consults, steps, shows the mixed
/// Prolog+C# stack, and runs goals in the Immediate window, exactly as it does for the
/// REPL.</para>
/// </summary>
public sealed class DebugOptions
{
    /// <summary>The <c>.pl</c> files the engine is ABOUT to consult, announced up front so a
    /// breakpoint drawn on one of them before the process has stopped anywhere still binds
    /// the first time it stops. Optional: every file is also announced to the debugger as it
    /// is consulted, so a host that just calls <see cref="PrologEngine.ConsultFile"/> as it
    /// goes needs this only when a launcher sets breakpoints before the engine runs at all.
    /// Relative paths are resolved against the current directory.</summary>
    public IReadOnlyList<string>? SourceFiles { get; set; }

    /// <summary>Last-call optimisation while debugging. OFF by default: LCO reclaims a
    /// caller's frame before the last call, and a frame that is gone is a frame the debugger
    /// cannot show — so a tail-recursive predicate would collapse to a single stack frame.
    /// Set it true to see the stack the release build would really have. (Overridden by the
    /// <c>SHUMWAY_DEBUG_LCO</c> environment pin when that is set.)</summary>
    public bool LastCallOptimisation { get; set; } = false;

    /// <summary>Block the calling thread until a debugger has attached AND finished arming
    /// the breakpoints it wants — the case for a process launched IN ORDER to be debugged
    /// from its first goal (the REPL's <c>--debug-wait</c>). Leave false for a host that runs
    /// normally and may be attached to at any moment; then this returns immediately and the
    /// debugger connects whenever it does.</summary>
    public bool WaitForAttach { get; set; } = false;

    /// <summary>ADR-036 — the DAP endpoint (VS Code). Null: none. 0: an ephemeral port
    /// (read it back from <see cref="ChannelDebugSession.DapPort"/>). N: listen on
    /// 127.0.0.1:N. Defaults from the <c>SHUMWAY_DAP_PORT</c> environment variable when
    /// that names a positive port — which is how a linked executable or any embedded host
    /// grows the endpoint with no code change (<c>=0</c> or unset leaves it off).</summary>
    public int? DapPort { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("SHUMWAY_DAP_PORT"), out int p)
            && p > 0 ? p : null;

    /// <summary>LAZY full debug: when true, the session opens with the RUNTIME debug
    /// machinery off — no ports raised, no trail-everything, last-call optimisation ON —
    /// so a debug-compiled program runs at near-release Tier-0 speed, and the machinery
    /// arms itself the moment a debugger actually ATTACHES (or the host calls
    /// <see cref="ChannelDebugSession.ActivateFullDebug"/>). What arming cannot recover
    /// is the PAST: frames LCO already reclaimed stay gone, and Set Next Statement can
    /// only rewind to points recorded after the attach. Code is compiled debuggable
    /// either way — debuggability of CODE is decided at compile time; this flag decides
    /// when the runtime starts PAYING for it.
    ///
    /// <para>The default comes from the <c>SHUMWAY_DEBUG_ACTIVATION</c> environment
    /// variable — <c>attach</c> for lazy, anything else (or unset) for full-from-startup
    /// — so a launcher can flip the default without the host changing code. Setting the
    /// property explicitly wins over the environment.</para></summary>
    public bool ActivateOnAttach { get; set; } =
        string.Equals(
            Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_ACTIVATION"),
            "attach", StringComparison.OrdinalIgnoreCase);

    /// <summary>How long <see cref="WaitForAttach"/> waits for the debugger to finish
    /// speaking before giving up and letting the program run. Ignored unless
    /// <see cref="WaitForAttach"/> is set.</summary>
    public TimeSpan AttachTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
