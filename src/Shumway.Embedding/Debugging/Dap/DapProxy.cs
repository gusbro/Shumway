using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;

namespace Shumway.Embedding.Debugging.Dap;

/// <summary>
/// ADR-036 — the debug ADAPTER: the small executable VS Code launches and speaks DAP
/// with over stdio (the declarative `program` field of the extension's debugger
/// contribution — no extension code at all). It is protocol glue on the IDE side, never
/// the debuggee: on <c>launch</c> it asks VS Code — the DAP <c>runInTerminal</c> reverse
/// request — to start the program in the integrated terminal (so the Prolog program
/// keeps its own console), then connects to the engine's in-process DAP endpoint
/// (<see cref="DapDebugServer"/>) and forwards; on <c>attach</c> it just connects.
///
/// <para>Forwarding is verbatim, byte for byte, in both directions. The adapter says only
/// three things of its own: the <c>initialize</c> response (the backend does not exist
/// yet — the capabilities here must state what <see cref="DapDebugServer"/> states), the
/// <c>launch</c>/<c>attach</c> response, and a <c>terminated</c> event if the debuggee
/// dies under the client. Its one private conversation with the backend — the
/// <c>initialize</c> it owes it — uses a sequence number far above any real client's
/// (<see cref="InternalSeqBase"/>), and the response to it is swallowed by that
/// number.</para>
/// </summary>
public sealed class DapProxy
{
    /// <summary>Adapter-internal requests to the backend live at and above this seq, so
    /// their responses are distinguishable from responses to forwarded client requests
    /// (which cross with the client's own small numbers).</summary>
    internal const int InternalSeqBase = 1_000_000;

    /// <summary>Reverse requests from the adapter to the CLIENT (runInTerminal) live at
    /// and above this seq, distinct from both ranges above.</summary>
    internal const int ReverseSeqBase = 900_000;

    private readonly Stream _clientIn;
    private readonly Stream _clientOut;
    private readonly object _clientWriteLock = new object();
    private readonly object _backendWriteLock = new object();
    private readonly Action<string> _log;

    private TcpClient? _backendTcp;
    private Stream? _backend;
    private Thread? _backendReader;
    private volatile bool _closing;
    private int _reverseSeq = ReverseSeqBase;

    // The response to a reverse request (runInTerminal), handed from the client read
    // loop to the launch handler waiting on it.
    private readonly SemaphoreSlim _reverseAnswered = new SemaphoreSlim(0);

    public DapProxy(Stream clientIn, Stream clientOut, Action<string>? log = null)
    {
        _clientIn = clientIn ?? throw new ArgumentNullException(nameof(clientIn));
        _clientOut = clientOut ?? throw new ArgumentNullException(nameof(clientOut));
        _log = log ?? (_ => { });
    }

    /// <summary>The adapter's life: read the client until it leaves. Returns when the
    /// session is over.</summary>
    public void Run()
    {
        try
        {
            while (!_closing)
            {
                byte[]? raw = DapWire.ReadMessageBytes(_clientIn);
                if (raw is null) break;
                using JsonDocument doc = JsonDocument.Parse(raw);
                HandleClientMessage(raw, doc.RootElement);
            }
        }
        catch (Exception ex)
        {
            _log("client read loop ended: " + ex.Message);
        }
        finally
        {
            _closing = true;
            CloseBackend();
        }
    }

    private void HandleClientMessage(byte[] raw, JsonElement root)
    {
        string type = root.TryGetProperty("type", out JsonElement t)
            ? t.GetString() ?? "" : "";

        // The client's answer to OUR runInTerminal: consume, wake the launch handler.
        if (type == "response"
            && root.TryGetProperty("request_seq", out JsonElement rs)
            && rs.GetInt32() >= ReverseSeqBase)
        {
            _reverseAnswered.Release();
            return;
        }

        if (type != "request")
        {
            Forward(raw);
            return;
        }

        int seq = root.GetProperty("seq").GetInt32();
        string command = root.GetProperty("command").GetString() ?? "";
        JsonElement args = root.TryGetProperty("arguments", out JsonElement a) ? a : default;

        switch (command)
        {
            case "initialize":
                // The backend does not exist yet; answer for it, with its capabilities —
                // this list MIRRORS DapDebugServer's initialize response, and the two
                // must say the same thing.
                SendToClient(w =>
                {
                    w.WriteString("type", "response");
                    w.WriteNumber("request_seq", seq);
                    w.WriteString("command", command);
                    w.WriteBoolean("success", true);
                    w.WriteStartObject("body");
                    w.WriteBoolean("supportsConfigurationDoneRequest", true);
                    w.WriteBoolean("supportsConditionalBreakpoints", true);
                    w.WriteBoolean("supportsSetVariable", true);
                    w.WriteBoolean("supportsEvaluateForHovers", true);
                    w.WriteBoolean("supportsGotoTargetsRequest", true);
                    w.WriteBoolean("supportsLogPoints", true);
                    w.WriteEndObject();
                });
                break;

            case "launch":
                HandleLaunch(seq, args);
                break;

            case "attach":
                HandleAttach(seq, args);
                break;

            case "disconnect":
                if (_backend is not null)
                {
                    Forward(raw);        // the backend's response flows back verbatim
                }
                else
                {
                    RespondOk(seq, command);
                }
                _closing = true;
                break;

            default:
                if (_backend is not null) Forward(raw);
                else
                    Respond(seq, command, false, "no debuggee is connected yet");
                break;
        }
    }

    // ----- launch / attach -----

    private void HandleLaunch(int seq, JsonElement args)
    {
        try
        {
            string shumwayPath = StringArg(args, "shumwayPath") ?? "shumway";
            string? program = StringArg(args, "program");
            string? goal = StringArg(args, "goal");
            string? cwd = StringArg(args, "cwd");
            int port = IntArg(args, "port") ?? PickFreePort();

            // --dap-wait, not --dap: a program LAUNCHED to be debugged holds its prompt
            // until the client's breakpoints are armed (configurationDone) — otherwise a
            // goal typed in the first second runs past every breakpoint (the launch
            // race). Attach keeps the plain, non-blocking --dap.
            var commandLine = new List<string> { shumwayPath, "--dap-wait", port.ToString() };
            if (program is not null) commandLine.Add(program);
            if (args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("args", out JsonElement extra)
                && extra.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in extra.EnumerateArray())
                    if (e.GetString() is string s) commandLine.Add(s);
            }
            if (goal is not null) { commandLine.Add("-g"); commandLine.Add(goal); }

            // The debuggee runs in VS Code's own terminal — its console stays its own,
            // and the DAP stdio this process is speaking stays ours.
            SendToClient(w =>
            {
                w.WriteString("type", "request");
                w.WriteString("command", "runInTerminal");
                w.WriteStartObject("arguments");
                w.WriteString("kind", "integrated");
                w.WriteString("title", "Shumway");
                if (cwd is not null) w.WriteString("cwd", cwd);
                else if (program is not null)
                    w.WriteString("cwd", Path.GetDirectoryName(Path.GetFullPath(program)) ?? ".");
                w.WriteStartArray("args");
                foreach (string part in commandLine) w.WriteStringValue(part);
                w.WriteEndArray();
                w.WriteEndObject();
            }, seqOverride: Interlocked.Increment(ref _reverseSeq));
            _reverseAnswered.Wait(TimeSpan.FromSeconds(10));

            ConnectBackend(port, TimeSpan.FromSeconds(20));
            RespondOk(seq, "launch");
        }
        catch (Exception ex)
        {
            Respond(seq, "launch", false, ex.Message);
        }
    }

    private void HandleAttach(int seq, JsonElement args)
    {
        try
        {
            int port = IntArg(args, "port")
                ?? throw new InvalidOperationException(
                    "attach needs a 'port' (the debuggee's --dap / SHUMWAY_DAP_PORT)");
            ConnectBackend(port, TimeSpan.FromSeconds(5));
            RespondOk(seq, "attach");
        }
        catch (Exception ex)
        {
            Respond(seq, "attach", false, ex.Message);
        }
    }

    /// <summary>Connects to the engine's DAP endpoint, retrying until the deadline (a
    /// just-launched debuggee takes a moment to open its listener), performs the
    /// initialize the backend is owed, and starts the pump that forwards everything it
    /// says to the client — including the `initialized` event the client is waiting for
    /// before it sends breakpoints.</summary>
    private void ConnectBackend(int port, TimeSpan deadline)
    {
        long until = Environment.TickCount64 + (long)deadline.TotalMilliseconds;
        Exception? last = null;
        while (Environment.TickCount64 < until)
        {
            try
            {
                var tcp = new TcpClient();
                tcp.Connect(IPAddress.Loopback, port);
                tcp.NoDelay = true;
                _backendTcp = tcp;
                _backend = tcp.GetStream();
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(250);
            }
        }
        if (_backend is null)
            throw new InvalidOperationException(
                "could not reach the debuggee's DAP endpoint on 127.0.0.1:" + port
                + (last is null ? "" : " (" + last.Message + ")"));

        SendToBackend(w =>
        {
            w.WriteString("type", "request");
            w.WriteString("command", "initialize");
            w.WriteStartObject("arguments");
            w.WriteString("adapterID", "shumway");
            w.WriteEndObject();
        }, InternalSeqBase);

        _backendReader = new Thread(BackendPump)
        {
            IsBackground = true,
            Name = "shumway-dap-proxy-backend",
        };
        _backendReader.Start();
        _log("connected to debuggee on port " + port);
    }

    /// <summary>Backend → client, verbatim — minus the response to the adapter's own
    /// initialize, which the client never asked for.</summary>
    private void BackendPump()
    {
        Stream backend = _backend!;
        try
        {
            while (!_closing)
            {
                byte[]? raw = DapWire.ReadMessageBytes(backend);
                if (raw is null) break;
                using JsonDocument doc = JsonDocument.Parse(raw);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("type", out JsonElement t)
                    && t.GetString() == "response"
                    && root.TryGetProperty("request_seq", out JsonElement rs)
                    && rs.GetInt32() >= InternalSeqBase)
                    continue;   // ours

                lock (_clientWriteLock)
                    DapWire.WriteMessage(_clientOut, raw);
            }
        }
        catch (Exception ex)
        {
            _log("backend pump ended: " + ex.Message);
        }

        // The debuggee died (or refused us) under a live client: say so, honestly,
        // instead of a session that hangs "running" forever.
        if (!_closing)
        {
            try
            {
                SendToClient(w =>
                {
                    w.WriteString("type", "event");
                    w.WriteString("event", "terminated");
                });
            }
            catch (Exception) { }
        }
    }

    // ----- plumbing -----

    private void Forward(byte[] raw)
    {
        Stream? backend = _backend;
        if (backend is null) return;
        lock (_backendWriteLock)
            DapWire.WriteMessage(backend, raw);
    }

    private void RespondOk(int requestSeq, string command)
        => Respond(requestSeq, command, true, null);

    private void Respond(int requestSeq, string command, bool success, string? message)
    {
        SendToClient(w =>
        {
            w.WriteString("type", "response");
            w.WriteNumber("request_seq", requestSeq);
            w.WriteString("command", command);
            w.WriteBoolean("success", success);
            if (message is not null) w.WriteString("message", message);
        });
    }

    private void SendToClient(Action<Utf8JsonWriter> fields, int? seqOverride = null)
    {
        byte[] json = Build(fields, seqOverride ?? Interlocked.Increment(ref _reverseSeq));
        lock (_clientWriteLock)
            DapWire.WriteMessage(_clientOut, json);
    }

    private void SendToBackend(Action<Utf8JsonWriter> fields, int seq)
    {
        byte[] json = Build(fields, seq);
        lock (_backendWriteLock)
            DapWire.WriteMessage(_backend!, json);
    }

    private static byte[] Build(Action<Utf8JsonWriter> fields, int seq)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", seq);
            fields(w);
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private void CloseBackend()
    {
        try { _backendTcp?.Close(); } catch (Exception) { }
        _backend = null;
        _backendTcp = null;
    }

    private static string? StringArg(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out JsonElement v)
            && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int? IntArg(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out JsonElement v)
            && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;

    private static int PickFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
