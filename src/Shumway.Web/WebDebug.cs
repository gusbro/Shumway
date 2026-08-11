using System.Runtime.InteropServices.JavaScript;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;

namespace Shumway.Web;

/// <summary>Debug mode. JavaScript is a frontend of <see cref="DebugService"/>
/// like Concord, DAP and the in-process tests — attached directly, no channel,
/// no protocol; the page calls the exports below and receives one stop event.
///
/// <para><b>Threading.</b> A stop happens INSIDE the running search — on the
/// pool thread <c>QueryNext</c> put it on, with <c>_engineGate</c> HELD. So the
/// stop handler cannot touch JavaScript directly (interop is thread-affine):
/// the snapshot is POSTED to the runtime thread, the road engine output already
/// takes. The handler then BLOCKS its thread until the page resumes it — the
/// pending <c>QueryNext</c> promise simply stays unresolved while stopped,
/// which is the truthful shape: the search has not answered. That is also why
/// <see cref="DebugResume"/> must NOT go through <c>OnEngine</c>: the gate is
/// held by the very query it needs to wake. Like <c>QueryCancel</c>, it only
/// flips state and returns.</para></summary>
internal static partial class WebShumwayApp
{
    private static DebugService? _debug;

    /// <summary>Blocks the stopped search until the page says how to go on.</summary>
    private static readonly SemaphoreSlim _debugResumeGate = new(0);

    /// <summary>1 while a stop is waiting to be resumed. Exchanged to 0 by the ONE
    /// release that wins — resume and cancel can both try, and a second Release
    /// would let the NEXT stop fall straight through.</summary>
    private static int _debugStopPending;

    private static string _debugResumeMode = "continue";

    [JSImport("ui.debugStopped", "main.js")]
    internal static partial void DebugStoppedToPage(string json);

    /// <summary>Restarts the engine with debug compilation on and a debug session
    /// attached. A fresh engine because debuggability is decided at COMPILE time:
    /// whatever was consulted before this call has no ports and no source map —
    /// the page consults the buffer again after enabling. Returns null, or the
    /// error text.</summary>
    [JSExport]
    internal static Task<string?> DebugEnable()
        => OnEngine(() =>
        {
            try
            {
                EndRun();
                _pageInput.SupplyEof();
                StartEngine();
                var engine = _session!.Engine;
                // Before any consult: the flag is read when a clause compiles —
                // and it must be set as a DIRECTIVE. A set_prolog_flag QUERY
                // lands in the store current_prolog_flag reads but not the one
                // consult compiles under; only the directive reaches that one.
                engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n");
                // A debugger needs the frames LCO would reclaim. This one is a
                // RUNTIME flag, set the way the debug test suites set it.
                foreach (var _ in engine.QueryAll("set_prolog_flag(debug_lco, off).")) { }
                _debug = new DebugService(engine, OnDebugStop);
                // Break All: the page sets a flag (DebugBreakNow); the engine
                // notices it at a port — the poll the desktop's F9-mid-run
                // rides — and stops right there through the normal handler.
                var debug = _debug;
                debug.Poll = () =>
                {
                    if (Interlocked.Exchange(ref _breakNowRequested, 0) == 1)
                        debug.BreakHereNow();
                };
                engine.AttachDebugSession(_debug);
                return (string?)null;
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        });

    /// <summary>Sets or removes a breakpoint, optionally guarded by a condition goal
    /// (empty = unconditional; a set REPLACES the previous condition — the page writes
    /// its whole desired state each time). Breakpoints bind by file BASE NAME, so the
    /// page passes the workspace file's name and it matches however the file was
    /// consulted. WHILE STOPPED the engine gate is held by the suspended search, so
    /// the call bypasses it — the engine is parked, and its breakpoint table is
    /// arm-gate-serialized on its own (the flow every desktop debugger uses).</summary>
    [JSExport]
    internal static Task<string?> DebugBreakpoint(string file, int line, bool set, string condition)
        => Volatile.Read(ref _debugStopPending) == 1
            ? Task.FromResult(BreakpointCore(file, line, set, condition, warmUp: false))
            : OnEngine(() => BreakpointCore(file, line, set, condition, warmUp: true));

    private static string? BreakpointCore(
        string file, int line, bool set, string condition, bool warmUp)
    {
        try
        {
            var engine = _session!.Engine;
            if (!set) { engine.RemoveBreakpoint(file, line); return null; }
            string? cond = condition.Length == 0 ? null : condition;
            int bound = engine.AddBreakpoint(file, line, cond);
            if (bound == 0 && warmUp)
            {
                // Once an engine has run a query, a consult defers compiling its
                // clauses to the NEXT query's setup — and a breakpoint only binds
                // to COMPILED sites. Force that setup and retry.
                foreach (var _ in engine.QueryAll("true.")) { }
                bound = engine.AddBreakpoint(file, line, cond);
            }
            return bound > 0
                ? null
                : "error: no debuggable code at " + file + ":" + line
                  + " (consult first, with debug enabled)";
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    /// <summary>The Immediate window: evaluates <paramref name="goal"/> against display
    /// frame <paramref name="frameIndex"/> of the SUSPENDED query — the engine-side
    /// semantics of the desktop debuggers, including the <c>!</c> on-frame prefix and a
    /// bare <c>;</c> for the next solution. Only meaningful while stopped. UNGATED (the
    /// stop holds the engine gate) but on a pool thread: an evaluation may run to its
    /// 15-second timeout, and the runtime thread must stay free.</summary>
    [JSExport]
    internal static Task<string> DebugEvaluate(int frameIndex, string goal)
    {
        var debug = _debug;
        if (debug is null || Volatile.Read(ref _debugStopPending) != 1)
            return Task.FromResult("nothing is stopped: no frame to evaluate against");
        return Task.Run(() =>
        {
            try { return debug.EvaluateGoal(frameIndex, goal); }
            catch (Exception ex) { return "error: " + ex.Message; }
        });
    }

    /// <summary>Re-captures the suspended query's frames — variables and residual
    /// constraints as they are NOW, after an on-frame <c>!</c> evaluation changed
    /// them. Same JSON as the stop event; empty when nothing is stopped.</summary>
    [JSExport]
    internal static Task<string> DebugFramesNow()
    {
        var debug = _debug;
        if (debug is null || Volatile.Read(ref _debugStopPending) != 1)
            return Task.FromResult("");
        return Task.Run(() =>
        {
            try
            {
                var now = debug.CaptureNow();
                return now is null ? "" : SerializeStop(now);
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        });
    }

    /// <summary>Wakes the stopped search: <c>continue</c>, <c>into</c>, <c>over</c>
    /// or <c>out</c>. False when nothing was stopped. Deliberately NOT gated — see
    /// the class comment.</summary>
    [JSExport]
    internal static Task<bool> DebugResume(string mode)
        => Task.FromResult(TryReleaseStop(mode));

    /// <summary>Asks the RUNNING search to pause at its next goal (Break All).
    /// Only a flag — the engine reads it at a port, on its own thread.</summary>
    [JSExport]
    internal static Task<bool> DebugBreakNow()
    {
        Interlocked.Exchange(ref _breakNowRequested, 1);
        return Task.FromResult(true);
    }

    /// <summary>1 while the page has asked for a Break All the engine has not yet
    /// honoured. Cleared when a run ends, so a pause requested too late cannot
    /// ambush the NEXT query at its first goal.</summary>
    private static int _breakNowRequested;

    /// <summary>Engine-gated normally, direct while a debug stop is pending: the
    /// suspended search HOLDS the gate, and the engine is parked — reading or
    /// writing workspace files then is safe and must not queue behind a gate
    /// that only the debugger's own resume will release. This is what lets the
    /// user browse the other files of the workspace while stopped.</summary>
    private static Task<T> OnEngineOrParked<T>(Func<T> work)
        => Volatile.Read(ref _debugStopPending) == 1
            ? Task.Run(work)
            : OnEngine(work);

    private static bool TryReleaseStop(string mode)
    {
        if (Interlocked.Exchange(ref _debugStopPending, 0) != 1) return false;
        _debugResumeMode = mode;
        _debugResumeGate.Release();
        return true;
    }

    private static void OnDebugStop(DebugService s, DebugStopEvent e)
    {
        // Pool thread, mid-search, engine gate held. Pending FIRST, so a resume
        // racing the post finds the stop already claimable.
        Interlocked.Exchange(ref _debugStopPending, 1);
        string json = SerializeStop(e);
        if (_jsThread is null || SynchronizationContext.Current == _jsThread)
            DebugStoppedToPage(json);
        else
            _jsThread.Post(j => DebugStoppedToPage((string)j!), json);

        // CA1416 flags any blocking wait as browser-unsupported, but that is a
        // statement about the RUNTIME thread. This handler only ever runs on
        // the pool thread the search occupies (OnEngine's Task.Run), where
        // blocking is legal — and blocking here is the point: stopped means
        // the search does not advance.
#pragma warning disable CA1416
        _debugResumeGate.Wait();
#pragma warning restore CA1416

        s.Resume(_debugResumeMode switch
        {
            "into" => StepMode.Into,
            "over" => StepMode.Over,
            "out" => StepMode.Out,
            _ => StepMode.Continue,
        });
    }

    private static string SerializeStop(DebugStopEvent e)
    {
        var json = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(json))
        {
            w.WriteStartObject();
            w.WriteString("reason", e.Reason.ToString().ToLowerInvariant());
            w.WriteString("goal", e.Goal);
            w.WriteString("file", e.File);
            w.WriteNumber("line", e.Line);
            // Where the USER's red dot is, when the stop is a breakpoint — a
            // head breakpoint binds at the clause's first goal, so File/Line
            // and BreakFile/BreakLine differ by design.
            w.WriteString("breakFile", e.BreakFile);
            w.WriteNumber("breakLine", e.BreakLine);
            // Why a CONDITIONAL breakpoint stopped without its condition
            // holding (it could not run); empty for every ordinary stop.
            w.WriteString("conditionError", e.ConditionError);
            w.WriteStartArray("frames");
            foreach (var f in e.Frames)
            {
                w.WriteStartObject();
                w.WriteString("name", f.Name);
                w.WriteNumber("arity", f.Arity);
                w.WriteString("headArgs", f.HeadArgs);
                // Which clause of its predicate is running, 1-based; 0 unknown.
                w.WriteNumber("clause", f.ClauseNumber);
                w.WriteString("file", f.File);
                w.WriteNumber("line", f.Line);
                w.WriteStartArray("vars");
                foreach (var (name, value) in f.Variables)
                {
                    w.WriteStartObject();
                    w.WriteString("name", name);
                    w.WriteString("value", value);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("residuals");
                foreach (var (name, goals) in f.Residuals)
                {
                    w.WriteStartObject();
                    w.WriteString("var", name);
                    w.WriteString("goals", goals);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(json.ToArray());
    }
}
