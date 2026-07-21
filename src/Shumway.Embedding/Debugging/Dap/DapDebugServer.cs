using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Shumway.Embedding.Debugging.Dap;

/// <summary>
/// ADR-036 — the VS Code frontend: a Debug Adapter Protocol server hosted IN-PROCESS by
/// the engine, over TCP on the loopback interface only.
///
/// <para>It is a peer of the Concord transport, not a replacement: it drives the same
/// <see cref="ChannelDebugSession"/> (breakpoints, conditions, stepping, snapshots — all
/// of ADR-035's engine core) through the session's external-driver seam. On a stop the
/// engine thread blocks inside <see cref="ChannelDebugSession.NotifyOverride"/> until the
/// DAP client says how to resume — where the VS transport traps into a hidden breakpoint,
/// this transport waits on a semaphore. Stack and variables requests are served on the
/// client's reader thread from the snapshot the engine wrote BEFORE stopping, so nothing
/// runs in the suspended machine to answer them — the same
/// write-before-notify discipline, without the pinned-memory indirection.</para>
///
/// <para><b>Both endpoints, one driver.</b> The server coexists with an attached Visual
/// Studio: it refuses a DAP client while a native debugger is attached, and the session
/// routes stops to the DAP client only while one is connected — first in, clean refusal
/// for the second. A disconnect is a detach: breakpoints cleared, lazy machinery
/// disarmed, the program runs free, and a new client may connect later.</para>
/// </summary>
public sealed class DapDebugServer : IDisposable
{
    private readonly ChannelDebugSession _session;
    private readonly TcpListener _listener;
    private readonly Thread _acceptThread;
    private volatile DapConnection? _connection;
    private volatile bool _disposed;

    /// <param name="session">The debug session to drive — the one
    /// <see cref="PrologEngine.EnableDebugging"/> returned.</param>
    /// <param name="port">The loopback port to listen on; 0 picks a free one (see
    /// <see cref="Port"/>).</param>
    public DapDebugServer(ChannelDebugSession session, int port = 0)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        // Register on the session whichever way we were constructed (StartDapServer or
        // directly), so DapPort / WaitForDapConfigured / dispose-with-the-session hold.
        session.AttachDapServer(this);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "shumway-dap-accept",
        };
        _acceptThread.Start();
        ShumwayDebugHelper.DiagLine("dap: listening on 127.0.0.1:" + Port);
    }

    /// <summary>The port actually bound — what a launcher passes to the client when the
    /// constructor was asked for an ephemeral one.</summary>
    public int Port { get; }

    /// <summary>Set when a client has sent <c>configurationDone</c> — its breakpoints
    /// are in the engine. What <c>--dap-wait</c> holds the program's start against: a
    /// prompt shown before this is a prompt that runs goals past every breakpoint the
    /// user drew (the launch race). Never reset — the door is held once, at birth.</summary>
    internal readonly ManualResetEventSlim ConfiguredEvent = new ManualResetEventSlim(false);

    /// <summary>Blocks until a client has connected AND finished configuring (or the
    /// timeout). The DAP form of ADR-035's "attached is not ready".</summary>
    public bool WaitUntilConfigured(TimeSpan timeout) => ConfiguredEvent.Wait(timeout);

    internal ChannelDebugSession Session => _session;

    private void AcceptLoop()
    {
        while (!_disposed)
        {
            TcpClient tcp;
            try { tcp = _listener.AcceptTcpClient(); }
            catch (Exception) { break; }   // listener closed: Dispose

            var conn = new DapConnection(this, tcp);

            // ONE driver. A native debugger already attached owns the session (ADR-036
            // arbitration), and so does an earlier DAP client. The loser gets a real DAP
            // answer — its initialize fails with the reason — not a slammed socket.
            if (System.Diagnostics.Debugger.IsAttached)
            {
                conn.RefuseAndClose("a debugger is already attached: Visual Studio");
                continue;
            }
            if (Interlocked.CompareExchange(ref _connection, conn, null) is not null)
            {
                conn.RefuseAndClose("a debugger is already attached: another DAP client");
                continue;
            }

            ShumwayDebugHelper.DiagLine("dap: client connected");
            _session.NotifyOverride = OnEngineStop;
            _session.ExternalDriverConnected = true;
            // A lazy session arms on connection, exactly as it arms on a native attach.
            // Idempotent for a session that opened fully armed.
            _session.ActivateFullDebug();

            // The reader loop gets its own thread: the accept loop must keep accepting,
            // because arbitration's clean refusal only exists for a client that gets
            // answered — one blocked behind the winner's session would just hang.
            var reader = new Thread(() =>
            {
                conn.Run();   // returns when the client leaves
                _session.ExternalDriverConnected = false;
                _session.NotifyOverride = null;
                _connection = null;
                ShumwayDebugHelper.DiagLine("dap: client disconnected");
            })
            {
                IsBackground = true,
                Name = "shumway-dap-client",
            };
            reader.Start();
        }
    }

    /// <summary>The session's stop, routed to the connected client. Runs on the ENGINE
    /// thread; blocks there until the client resumes. False = no client (or it left
    /// mid-stop), which the session treats as a detach.</summary>
    private bool OnEngineStop(int reason)
    {
        DapConnection? conn = _connection;
        return conn is not null && conn.HandleStop((StopReason)reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener.Stop(); } catch (Exception) { }
        _connection?.Drop();
        try { _acceptThread.Join(2000); } catch (Exception) { }
    }
}

/// <summary>One connected DAP client: the reader loop, the request handlers, and the
/// stop/resume handshake with the engine thread.</summary>
internal sealed class DapConnection
{
    private readonly DapDebugServer _server;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly object _writeLock = new object();
    private int _seq;
    private volatile bool _gone;

    // ----- the stop handshake (engine thread ⇄ reader thread) -----
    private readonly SemaphoreSlim _resume = new SemaphoreSlim(0);
    private bool _stopped;                     // guarded by _stateLock

    // ----- the debugger's full desired state, rewritten to the channel on every change
    // (the same idempotent full-state model the Concord component uses) -----
    private readonly object _stateLock = new object();
    private readonly Dictionary<string, List<(int Line, string Condition)>> _breakpoints
        = new Dictionary<string, List<(int, string)>>(StringComparer.OrdinalIgnoreCase);
    private DebugCommand? _pendingResume;

    // ----- ADR-036 V5: logpoints -----
    // A breakpoint with a log message stops the MACHINE (the engine's ordinary Break)
    // but never the USER: the stop is answered here with an output event and an
    // immediate resume, and no `stopped` ever reaches the client. Keyed by the
    // (file, line) THE CLIENT SET — which is exactly what the snapshot's
    // BreakFile/BreakLine report a hit as.
    private readonly Dictionary<string, string> _logpoints
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static string LogKey(string file, int line) => file + "|" + line;

    // ----- ADR-036 V4: Jump to Cursor -----
    // DAP's goto carries no frame, so the target frame is the Call Stack SELECTION,
    // inferred from the last `scopes` request — the same inference the VS frontend
    // formalised (GetFrameLocals → MsgSelectedFrame). Targets minted by gotoTargets
    // live until the next stop.
    private int _selectedFrame;                                  // 0-based
    private readonly List<(int Frame, int Line)> _gotoTargets
        = new List<(int, int)>();

    internal DapConnection(DapDebugServer server, TcpClient tcp)
    {
        _server = server;
        _tcp = tcp;
        tcp.NoDelay = true;
        _stream = tcp.GetStream();
    }

    // ----- lifecycle -----

    /// <summary>The reader loop. Requests are handled on this thread — including while
    /// the engine thread is blocked in a stop, which is precisely when stackTrace,
    /// scopes and variables arrive.</summary>
    internal void Run()
    {
        try
        {
            while (!_gone)
            {
                using JsonDocument? doc = DapWire.ReadMessage(_stream);
                if (doc is null) break;
                Handle(doc.RootElement);
            }
        }
        catch (Exception ex)
        {
            // Usually a torn socket = the client leaving; under diagnostics, say WHICH
            // exception — a parse or handler failure dying here looks identical to a
            // disconnect and is undiagnosable without this line.
            ShumwayDebugHelper.DiagLine(
                "dap: read loop ended: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            Drop();
        }
    }

    /// <summary>The client is gone (or being refused service): free a blocked engine
    /// thread — <see cref="HandleStop"/> then returns false and the session disarms —
    /// and close the socket.</summary>
    internal void Drop()
    {
        _gone = true;
        lock (_stateLock)
        {
            if (_stopped)
            {
                _stopped = false;
                _resume.Release();
            }
        }
        try { _tcp.Close(); } catch (Exception) { }
    }

    /// <summary>Arbitration's loser: answer its initialize honestly, then close.</summary>
    internal void RefuseAndClose(string reason)
    {
        try
        {
            using JsonDocument? doc = DapWire.ReadMessage(_stream);
            if (doc is not null
                && doc.RootElement.TryGetProperty("seq", out JsonElement seq))
                SendResponse(seq.GetInt32(),
                    doc.RootElement.TryGetProperty("command", out JsonElement c)
                        ? c.GetString() ?? "initialize" : "initialize",
                    success: false, message: reason);
        }
        catch (Exception) { }
        try { _tcp.Close(); } catch (Exception) { }
    }

    // ----- the stop (engine thread) -----

    internal bool HandleStop(StopReason reason)
    {
        if (_gone) return false;
        switch (reason)
        {
            // Internal notifications, not stops to show: the engine resumes at once.
            case StopReason.SourcesChanged:
                return true;
            // A step ran off the end of the program: tell the client it is running,
            // or its UI stays in "stepping" forever (the VS lesson, StopReason docs).
            case StopReason.StepAbandoned:
                SendEvent("continued", w =>
                {
                    w.WriteNumber("threadId", 1);
                    w.WriteBoolean("allThreadsContinued", true);
                });
                return true;
        }

        // A logpoint's hit: say its message, never its stop. The empty command drain
        // after this return resumes the machine in plain continue.
        if (reason == StopReason.Breakpoint)
        {
            DebugSnapshot? snap = _server.Session.Channel.ReadSnapshot();
            string? message = null;
            if (snap is { BreakFile.Length: > 0 })
                lock (_stateLock)
                    _logpoints.TryGetValue(LogKey(snap.BreakFile, snap.BreakLine),
                        out message);
            if (message is not null && snap is not null)
            {
                string text = InterpolateLogMessage(message, snap);
                SendEvent("output", w =>
                {
                    w.WriteString("category", "console");
                    w.WriteString("output", text + "\n");
                });
                return true;
            }
        }

        lock (_stateLock)
        {
            _pendingResume = null;   // the previous resume was consumed by reaching here
            _stopped = true;
            _selectedFrame = 0;      // a fresh stop selects its top frame
            _gotoTargets.Clear();    // stale targets must not move a different stop
        }
        SendStopped(reason);
        _resume.Wait();
        return !_gone;
    }

    private void SendStopped(StopReason reason)
    {
        (string dapReason, string? description) = reason switch
        {
            StopReason.Breakpoint => ("breakpoint", null),
            StopReason.AsyncBreak => ("pause", null),
            StopReason.Call => ("step", "call port"),
            StopReason.Exit => ("step", "exit port"),
            StopReason.Redo => ("step", "redo port"),
            StopReason.Fail => ("step", "fail port"),
            _ => ("step", null),
        };
        SendEvent("stopped", w =>
        {
            w.WriteString("reason", dapReason);
            if (description is not null) w.WriteString("description", description);
            w.WriteNumber("threadId", 1);
            w.WriteBoolean("allThreadsStopped", true);
        });
    }

    // ----- requests (reader thread) -----

    private void Handle(JsonElement root)
    {
        if (!root.TryGetProperty("type", out JsonElement type)
            || type.GetString() != "request")
            return;
        int seq = root.GetProperty("seq").GetInt32();
        string command = root.GetProperty("command").GetString() ?? "";
        JsonElement args = root.TryGetProperty("arguments", out JsonElement a) ? a : default;

        try
        {
            switch (command)
            {
                case "initialize":
                    // These capabilities are MIRRORED by DapProxy's own initialize
                    // response (the adapter answers before this server exists) — the
                    // two lists must say the same thing.
                    SendResponse(seq, command, true, w =>
                    {
                        w.WriteBoolean("supportsConfigurationDoneRequest", true);
                        w.WriteBoolean("supportsConditionalBreakpoints", true);
                        w.WriteBoolean("supportsSetVariable", true);
                        w.WriteBoolean("supportsEvaluateForHovers", true);
                        w.WriteBoolean("supportsGotoTargetsRequest", true);
                        w.WriteBoolean("supportsLogPoints", true);
                    });
                    SendEvent("initialized");
                    break;

                // V1: the program is driven by the host (launch integration is V2/V5);
                // both requests are acknowledged so any client can open a session.
                case "launch":
                case "attach":
                    SendResponse(seq, command, true);
                    break;

                case "configurationDone":
                    // Hold the door (the DAP form of WaitForDebuggerCommands): the client
                    // is about to let the program run, and the breakpoints it just sent
                    // are still in the command region until the engine's idle watcher or
                    // poll drains them. Answer once they are ARMED, so a program started
                    // on this response cannot run past them. Timeboxed: an engine blocked
                    // in a stop drains at resume instead, and a client is never hung.
                    for (int i = 0; i < 200; i++)
                    {
                        bool stopped;
                        lock (_stateLock) stopped = _stopped;
                        if (_gone || stopped
                            || _server.Session.Channel.CommandsConsumed) break;
                        Thread.Sleep(10);
                    }
                    SendResponse(seq, command, true);
                    // --dap-wait releases the program's start on this: the client's
                    // breakpoints are armed, running is now safe.
                    _server.ConfiguredEvent.Set();
                    break;

                case "setBreakpoints":
                    HandleSetBreakpoints(seq, args);
                    break;

                case "setExceptionBreakpoints":
                    SendResponse(seq, command, true, w =>
                    {
                        w.WriteStartArray("breakpoints");
                        w.WriteEndArray();
                    });
                    break;

                case "threads":
                    SendResponse(seq, command, true, w =>
                    {
                        w.WriteStartArray("threads");
                        w.WriteStartObject();
                        w.WriteNumber("id", 1);
                        w.WriteString("name", "Prolog");
                        w.WriteEndObject();
                        w.WriteEndArray();
                    });
                    break;

                case "stackTrace":
                    HandleStackTrace(seq, args);
                    break;

                case "scopes":
                    HandleScopes(seq, args);
                    break;

                case "variables":
                    HandleVariables(seq, args);
                    break;

                case "continue":
                    HandleResume(seq, command, DebugCommandKind.Continue,
                        w => w.WriteBoolean("allThreadsContinued", true));
                    break;
                case "next":
                    HandleResume(seq, command, DebugCommandKind.StepOver);
                    break;
                case "stepIn":
                    HandleResume(seq, command, DebugCommandKind.StepInto);
                    break;
                case "stepOut":
                    HandleResume(seq, command, DebugCommandKind.StepOut);
                    break;

                case "gotoTargets":
                    HandleGotoTargets(seq, args);
                    break;

                case "goto":
                    HandleGoto(seq, args);
                    break;

                case "evaluate":
                    HandleEvaluate(seq, args);
                    break;

                case "setVariable":
                    HandleSetVariable(seq, args);
                    break;

                case "pause":
                    HandlePause(seq);
                    break;

                case "disconnect":
                    HandleDisconnect(seq);
                    break;

                default:
                    SendResponse(seq, command, false,
                        message: "unsupported request '" + command + "'");
                    break;
            }
        }
        catch (Exception ex)
        {
            SendResponse(seq, command, false, message: ex.Message);
        }
    }

    private void HandleSetBreakpoints(int seq, JsonElement args)
    {
        string path = "";
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("source", out JsonElement source))
        {
            if (source.TryGetProperty("path", out JsonElement p)) path = p.GetString() ?? "";
            else if (source.TryGetProperty("name", out JsonElement n)) path = n.GetString() ?? "";
        }

        var wanted = new List<(int Line, string Condition)>();
        var logs = new List<(int Line, string Message)>();
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("breakpoints", out JsonElement bps)
            && bps.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement bp in bps.EnumerateArray())
            {
                int line = bp.GetProperty("line").GetInt32();
                string condition = bp.TryGetProperty("condition", out JsonElement cond)
                    ? cond.GetString() ?? "" : "";
                wanted.Add((line, condition));
                if (bp.TryGetProperty("logMessage", out JsonElement lm)
                    && lm.GetString() is { Length: > 0 } message)
                    logs.Add((line, message));
            }
        }

        lock (_stateLock)
        {
            // DAP semantics: this request REPLACES the file's breakpoints.
            if (wanted.Count == 0) _breakpoints.Remove(path);
            else _breakpoints[path] = wanted;
            foreach (string stale in _logpoints.Keys
                .Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase))
                .ToList())
                _logpoints.Remove(stale);
            foreach ((int line, string message) in logs)
                _logpoints[LogKey(path, line)] = message;
            WriteChannelState();
        }

        // V1 answers verified optimistically; a hollow-breakpoint refinement (the
        // engine's bind count travels back on a later `breakpoint` event) is deferred.
        SendResponse(seq, "setBreakpoints", true, w =>
        {
            w.WriteStartArray("breakpoints");
            foreach ((int line, _) in wanted)
            {
                w.WriteStartObject();
                w.WriteBoolean("verified", true);
                w.WriteNumber("line", line);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    private void HandleStackTrace(int seq, JsonElement args)
    {
        DebugSnapshot? snapshot = _server.Session.Channel.ReadSnapshot();
        IReadOnlyList<DebugSnapshotFrame> frames =
            snapshot is { Running: false } ? snapshot.Frames
            : Array.Empty<DebugSnapshotFrame>();

        int start = args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("startFrame", out JsonElement sf) ? sf.GetInt32() : 0;
        int levels = args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("levels", out JsonElement lv) && lv.GetInt32() > 0
            ? lv.GetInt32() : int.MaxValue;

        SendResponse(seq, "stackTrace", true, w =>
        {
            w.WriteStartArray("stackFrames");
            for (int i = start; i < frames.Count && i - start < levels; i++)
            {
                DebugSnapshotFrame f = frames[i];
                w.WriteStartObject();
                w.WriteNumber("id", i + 1);
                w.WriteString("name",
                    f.HeadArgs.Length > 0 ? f.Name + f.HeadArgs : f.Name + "/" + f.Arity);
                if (f.File.Length > 0)
                {
                    w.WriteStartObject("source");
                    w.WriteString("name", Path.GetFileName(f.File));
                    w.WriteString("path", f.File);
                    w.WriteEndObject();
                }
                w.WriteNumber("line", f.Line);
                w.WriteNumber("column", f.Line > 0 ? 1 : 0);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("totalFrames", frames.Count);
        });
    }

    private void HandleScopes(int seq, JsonElement args)
    {
        int frameId = args.GetProperty("frameId").GetInt32();
        // The selection signal: VS Code fetches a frame's scopes when the user selects
        // it in the Call Stack — which is what a later Jump to Cursor targets.
        lock (_stateLock)
            if (frameId >= 1) _selectedFrame = frameId - 1;
        SendResponse(seq, "scopes", true, w =>
        {
            w.WriteStartArray("scopes");
            w.WriteStartObject();
            w.WriteString("name", "Locals");
            // The frame id doubles as the variables reference: frames and their
            // variable sets are 1:1, and both die with the stop that minted them.
            w.WriteNumber("variablesReference", frameId);
            w.WriteBoolean("expensive", false);
            w.WriteEndObject();
            w.WriteEndArray();
        });
    }

    private void HandleVariables(int seq, JsonElement args)
    {
        int reference = args.GetProperty("variablesReference").GetInt32();
        DebugSnapshot? snapshot = _server.Session.Channel.ReadSnapshot();
        IReadOnlyList<DebugVariableView> vars =
            snapshot is { Running: false } && reference >= 1
                && reference <= snapshot.Frames.Count
            ? snapshot.Frames[reference - 1].Variables
            : Array.Empty<DebugVariableView>();

        SendResponse(seq, "variables", true, w =>
        {
            w.WriteStartArray("variables");
            foreach (DebugVariableView v in vars)
            {
                w.WriteStartObject();
                w.WriteString("name", v.Name);
                w.WriteString("value", v.Value);
                // Values are rendered whole (writeq) by the engine at the stop; lazy
                // subterm expansion is a leaf-honesty decision inherited from the VS EE.
                w.WriteNumber("variablesReference", 0);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    private void HandleResume(
        int seq, string command, DebugCommandKind kind, Action<Utf8JsonWriter>? body = null)
    {
        lock (_stateLock)
        {
            if (_stopped)
            {
                _pendingResume = new DebugCommand(kind);
                WriteChannelState(freshResume: true);
                _stopped = false;
                _resume.Release();
            }
            // Not stopped: nothing to resume. Answer success — a client and an engine
            // can legitimately disagree by one message in flight.
        }
        SendResponse(seq, command, true, body);
    }

    /// <summary>ADR-036 V4 — Jump to Cursor, step 1: which lines can the arrow move to?
    /// Valid targets are what the engine PUBLISHED for this stop (the per-frame
    /// SetNextLines the marks machinery derives — ADR-035's Set Next Statement), read
    /// for the SELECTED frame. An empty answer is how the editor greys the action.</summary>
    private void HandleGotoTargets(int seq, JsonElement args)
    {
        int line = args.GetProperty("line").GetInt32();

        DebugSnapshot? snapshot = _server.Session.Channel.ReadSnapshot();
        bool valid = false;
        int frame;
        lock (_stateLock)
        {
            frame = _selectedFrame;
            if (_stopped && snapshot is { Running: false } && frame < snapshot.Frames.Count)
            {
                IReadOnlyList<int> lines = snapshot.Frames[frame].SetNextLines;
                for (int i = 0; i < lines.Count && !valid; i++)
                    valid = lines[i] == line;
                if (valid) _gotoTargets.Add((frame, line));
            }
        }

        SendResponse(seq, "gotoTargets", true, w =>
        {
            w.WriteStartArray("targets");
            if (valid)
            {
                w.WriteStartObject();
                w.WriteNumber("id", _gotoTargets.Count);   // minted under the lock above
                w.WriteString("label", "line " + line);
                w.WriteNumber("line", line);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    /// <summary>ADR-036 V4 — Jump to Cursor, step 2: the move itself. Runs the ADR-035
    /// Set Next Statement (forward skips; backward rewinds the trail to the port mark,
    /// undoing the bindings since) directly on this thread — the engine thread is parked
    /// in the stop — and the session bracket re-captures the snapshot, so the
    /// <c>stopped(goto)</c> event makes the client re-read a stack that is already
    /// standing on the new line. The machine consumes the redirect when it resumes. A
    /// refusal is an honest error, never a silent no-op.</summary>
    private void HandleGoto(int seq, JsonElement args)
    {
        int targetId = args.GetProperty("targetId").GetInt32();

        int frame, line;
        lock (_stateLock)
        {
            if (!_stopped || targetId < 1 || targetId > _gotoTargets.Count)
            {
                SendResponse(seq, "goto", false, message: "no such goto target");
                return;
            }
            (frame, line) = _gotoTargets[targetId - 1];
        }

        string result = _server.Session.SetNextStatement(frame, line);
        if (result.Length != 0)
        {
            SendResponse(seq, "goto", false, message: result.Replace('\n', ' '));
            return;
        }

        lock (_stateLock)
        {
            _selectedFrame = 0;      // the move re-shapes the stack; select its new top
            _gotoTargets.Clear();
        }
        SendResponse(seq, "goto", true);
        SendEvent("stopped", w =>
        {
            w.WriteString("reason", "goto");
            w.WriteNumber("threadId", 1);
            w.WriteBoolean("allThreadsStopped", true);
        });
    }

    /// <summary>ADR-036 V3 — the Debug Console (context "repl") is the Immediate window:
    /// the goal runs in the LIVE suspended engine, bindings can be committed into the
    /// frame, and "<c>;</c>" pumps the next solution — the engine machinery is ADR-035's,
    /// called directly from this thread while the engine thread is parked in the stop.
    /// Every other context (watch, hover) is NoSideEffects: it answers only frame
    /// VARIABLES, from the snapshot — hovering a predicate name must not run it (the
    /// DataTip lesson).</summary>
    private void HandleEvaluate(int seq, JsonElement args)
    {
        string expression = StringOf(args, "expression")?.Trim() ?? "";
        string context = StringOf(args, "context") ?? "repl";
        int frameIndex = args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("frameId", out JsonElement f)
            ? Math.Max(0, f.GetInt32() - 1) : 0;

        bool stopped;
        lock (_stateLock) stopped = _stopped;

        if (context != "repl")
        {
            DebugSnapshot? snapshot = _server.Session.Channel.ReadSnapshot();
            DebugVariableView? found =
                snapshot is { Running: false } && frameIndex < snapshot.Frames.Count
                ? FindVariable(snapshot.Frames[frameIndex], expression) : null;
            if (found is null)
            {
                SendResponse(seq, "evaluate", false,
                    message: "not a frame variable (goals run in the Debug Console)");
                return;
            }
            SendResponse(seq, "evaluate", true, w =>
            {
                w.WriteString("result", found.Value);
                w.WriteNumber("variablesReference", 0);
            });
            return;
        }

        if (!stopped)
        {
            SendResponse(seq, "evaluate", false,
                message: "not stopped — goals evaluate at a breakpoint");
            return;
        }

        // A bare frame-variable name prints its value, as in the Visual Studio
        // Immediate window — asking after `X` is a question, not a goal to run.
        {
            DebugSnapshot? snapshot = _server.Session.Channel.ReadSnapshot();
            DebugVariableView? variable =
                snapshot is { Running: false } && frameIndex < snapshot.Frames.Count
                ? FindVariable(snapshot.Frames[frameIndex], expression) : null;
            if (variable is not null)
            {
                SendResponse(seq, "evaluate", true, w =>
                {
                    w.WriteString("result", variable.Value);
                    w.WriteNumber("variablesReference", 0);
                });
                return;
            }
        }

        // The engine thread is parked inside the stop holding its gate, so a NESTED stop
        // raised by the evaluated goal — routed to this thread — would deadlock against
        // it. Under DAP an evaluation therefore runs straight through breakpoints (the
        // engine's documented suppression mode); a nested VS-style break state has no
        // DAP shape anyway.
        bool suppress = DebugService.SuppressStopsDuringEvaluation;
        DebugService.SuppressStopsDuringEvaluation = true;
        string result;
        try
        {
            result = _server.Session.EvaluateGoal(frameIndex, expression);
        }
        finally
        {
            DebugService.SuppressStopsDuringEvaluation = suppress;
        }

        SendResponse(seq, "evaluate", true, w =>
        {
            w.WriteString("result", result);
            w.WriteNumber("variablesReference", 0);
        });

        // A commit into the frame re-captured the snapshot: tell the client its
        // variables are stale, so Locals refresh without waiting for the next stop.
        SendEvent("invalidated", w =>
        {
            w.WriteStartArray("areas");
            w.WriteStringValue("variables");
            w.WriteEndArray();
        });
    }

    /// <summary>ADR-036 V3 — the destructive Watch/Locals edit, verbatim ADR-035
    /// semantics: a bound variable is re-bound (trailed, so backtracking restores it),
    /// <c>_</c> un-instantiates, the new term may name the frame's other variables.</summary>
    private void HandleSetVariable(int seq, JsonElement args)
    {
        int reference = args.GetProperty("variablesReference").GetInt32();
        string name = args.GetProperty("name").GetString() ?? "";
        string value = args.GetProperty("value").GetString() ?? "";

        bool stopped;
        lock (_stateLock) stopped = _stopped;
        if (!stopped)
        {
            SendResponse(seq, "setVariable", false, message: "not stopped");
            return;
        }

        bool suppress = DebugService.SuppressStopsDuringEvaluation;
        DebugService.SuppressStopsDuringEvaluation = true;
        string result;
        try
        {
            result = _server.Session.SetFrameVariable(reference - 1, name, value);
        }
        finally
        {
            DebugService.SuppressStopsDuringEvaluation = suppress;
        }

        if (result.Length != 0)
        {
            SendResponse(seq, "setVariable", false, message: result.Replace('\n', ' '));
            return;
        }

        // The session re-captured the snapshot; answer with the value as the engine now
        // renders it (writeq — what round-trips), not as the user happened to type it.
        DebugSnapshot? fresh = _server.Session.Channel.ReadSnapshot();
        DebugVariableView? nowValue =
            fresh is not null && reference >= 1 && reference <= fresh.Frames.Count
            ? FindVariable(fresh.Frames[reference - 1], name) : null;
        SendResponse(seq, "setVariable", true, w =>
            w.WriteString("value", nowValue?.Value ?? value));
    }

    /// <summary>ADR-036 V5 — a log message's <c>{Name}</c> holes filled with the top
    /// frame's variable values (writeq-rendered, from the snapshot — nothing runs). An
    /// unknown name stays as typed, which is also how a literal brace survives.</summary>
    private string InterpolateLogMessage(string message, DebugSnapshot snapshot)
    {
        DebugSnapshotFrame? top = snapshot.Frames.Count > 0 ? snapshot.Frames[0] : null;
        var result = new StringBuilder(message.Length + 16);
        int at = 0;
        while (at < message.Length)
        {
            int open = message.IndexOf('{', at);
            if (open < 0) { result.Append(message, at, message.Length - at); break; }
            int close = message.IndexOf('}', open + 1);
            if (close < 0) { result.Append(message, at, message.Length - at); break; }
            result.Append(message, at, open - at);
            string name = message.Substring(open + 1, close - open - 1).Trim();
            DebugVariableView? variable =
                top is not null ? FindVariable(top, name) : null;
            if (variable is not null) result.Append(variable.Value);
            else result.Append(message, open, close - open + 1);   // as typed
            at = close + 1;
        }
        return result.ToString();
    }

    private static DebugVariableView? FindVariable(DebugSnapshotFrame frame, string name)
    {
        foreach (DebugVariableView v in frame.Variables)
            if (v.Name == name) return v;
        return null;
    }

    private static string? StringOf(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out JsonElement v)
            && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private void HandlePause(int seq)
    {
        lock (_stateLock)
        {
            if (!_stopped)
            {
                // BreakNow = stop at the next port, where a stack MEANS something. The
                // running engine's poll drains it; the idle watcher answers for an
                // engine that is standing still.
                WriteChannelState(new DebugCommand(DebugCommandKind.BreakNow));
            }
        }
        SendResponse(seq, "pause", true);
    }

    private void HandleDisconnect(int seq)
    {
        lock (_stateLock)
        {
            _breakpoints.Clear();
            _pendingResume = new DebugCommand(DebugCommandKind.Continue);
            WriteChannelState(freshResume: true);
        }
        SendResponse(seq, "disconnect", true);
        Drop();   // releases a blocked engine; Run() then unwires the session
    }

    /// <summary>Rewrites the engine's command region with the client's FULL desired state:
    /// clear + every breakpoint of every file, then the one-shot commands. The same
    /// idempotent model the Concord component uses — a region, not a queue, so every
    /// write must carry everything. Callers hold <see cref="_stateLock"/>.</summary>
    /// <param name="freshResume">True when the caller has JUST set
    /// <see cref="_pendingResume"/> — which must ride this write. Only a rewrite that
    /// carries no new resume may retire a drained one-shot: the region reads as consumed
    /// the whole time between two stops, and dropping the fresh step on that evidence is
    /// how "next" silently became "continue".</param>
    private void WriteChannelState(DebugCommand? extra = null, bool freshResume = false)
    {
        DebugChannel channel = _server.Session.Channel;

        // A one-shot the engine already drained must not ride this rewrite: a stale
        // step re-applied while idle would arm a step nobody asked for.
        if (!freshResume && _pendingResume is not null && channel.CommandsConsumed)
            _pendingResume = null;

        var commands = new List<DebugCommand>
        {
            new DebugCommand(DebugCommandKind.ClearBreakpoints),
        };
        foreach (KeyValuePair<string, List<(int Line, string Condition)>> file in _breakpoints)
            foreach ((int line, string condition) in file.Value)
                commands.Add(new DebugCommand(
                    DebugCommandKind.AddBreakpoint, file.Key, line, Condition: condition));
        if (_pendingResume is DebugCommand resume) commands.Add(resume);
        if (extra is DebugCommand one) commands.Add(one);

        channel.WriteCommands(commands.ToArray());
    }

    // ----- outgoing messages -----

    private void SendResponse(
        int requestSeq, string command, bool success,
        Action<Utf8JsonWriter>? body = null, string? message = null)
    {
        Send(w =>
        {
            w.WriteString("type", "response");
            w.WriteNumber("request_seq", requestSeq);
            w.WriteString("command", command);
            w.WriteBoolean("success", success);
            if (message is not null) w.WriteString("message", message);
            if (body is not null)
            {
                w.WriteStartObject("body");
                body(w);
                w.WriteEndObject();
            }
        });
    }

    private void SendEvent(string name, Action<Utf8JsonWriter>? body = null)
    {
        Send(w =>
        {
            w.WriteString("type", "event");
            w.WriteString("event", name);
            if (body is not null)
            {
                w.WriteStartObject("body");
                body(w);
                w.WriteEndObject();
            }
        });
    }

    private void Send(Action<Utf8JsonWriter> writeFields)
    {
        if (_gone) return;
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", Interlocked.Increment(ref _seq));
            writeFields(w);
            w.WriteEndObject();
        }
        try
        {
            lock (_writeLock)
                DapWire.WriteMessage(_stream, buffer.ToArray());
        }
        catch (Exception)
        {
            // The client left mid-write; the reader loop notices on its next read.
            _gone = true;
        }
    }
}
