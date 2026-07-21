using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-036 test infrastructure — the debuggee's query, on its own thread (the
/// engine thread the stops will block). A plain thread (not a Task) so a test that joins
/// with a timeout is not a blocking-task-operation the analyzer flags.</summary>
internal sealed class QueryRun
{
    private readonly Thread _thread;
    public List<Solution>? Solutions;
    public Exception? Error;

    public QueryRun(PrologEngine engine, string goal)
    {
        _thread = new Thread(() =>
        {
            try { Solutions = engine.QueryAll(goal).ToList(); }
            catch (Exception ex) { Error = ex; }
        })
        { IsBackground = true, Name = "dap-test-query" };
        _thread.Start();
    }

    public bool Join(int timeoutMs)
    {
        bool done = _thread.Join(timeoutMs);
        if (done && Error is not null)
            throw new InvalidOperationException("query failed: " + Error.Message, Error);
        return done;
    }
}

/// <summary>ADR-036 test infrastructure — a minimal DAP client: its own framing and
/// JSON, deliberately sharing no code with the server or the proxy — the tests prove the
/// wire, not the implementation against itself. Speaks over TCP (the server tests) or
/// over a stream pair (the proxy tests, where it plays VS Code on the adapter's
/// stdio).</summary>
internal sealed class DapTestClient : IDisposable
{
    private readonly TcpClient? _tcp;
    private readonly Stream _in;
    private readonly Stream _out;
    private readonly Thread _reader;
    private readonly BlockingCollection<JsonDocument> _incoming
        = new BlockingCollection<JsonDocument>();
    private readonly List<JsonDocument> _stashed = new List<JsonDocument>();
    private int _seq;
    private volatile bool _disposed;

    public DapTestClient(int port)
    {
        _tcp = new TcpClient();
        _tcp.Connect("127.0.0.1", port);
        NetworkStream stream = _tcp.GetStream();
        _in = stream;
        _out = stream;
        _reader = StartReader();
    }

    /// <summary>The proxy shape: this client reads what the adapter writes and writes
    /// what it reads — VS Code on the two halves of the adapter's stdio.</summary>
    public DapTestClient(Stream input, Stream output)
    {
        _in = input;
        _out = output;
        _reader = StartReader();
    }

    private Thread StartReader()
    {
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "dap-test-reader" };
        reader.Start();
        return reader;
    }

    public JsonElement Request(
        string command, string? argsJson = null, bool expectSuccess = true)
    {
        int seq = ++_seq;
        SendRaw("{\"seq\":" + seq + ",\"type\":\"request\",\"command\":\"" + command + "\""
            + (argsJson is null ? "" : ",\"arguments\":" + argsJson) + "}");

        JsonDocument response = Take(
            d => d.RootElement.GetProperty("type").GetString() == "response"
                && d.RootElement.TryGetProperty("request_seq", out JsonElement rs)
                && rs.GetInt32() == seq,
            "response to '" + command + "'");
        JsonElement root = response.RootElement;
        if (expectSuccess)
            Assert.True(root.GetProperty("success").GetBoolean(),
                "'" + command + "' failed: "
                + (root.TryGetProperty("message", out JsonElement m)
                    ? m.GetString() : "(no message)"));
        return root;
    }

    /// <summary>Fire-and-forget request — for driving a response the caller collects
    /// later (a launch whose reply only comes after the reverse-request dance).</summary>
    public int RequestAsync(string command, string? argsJson = null)
    {
        int seq = ++_seq;
        SendRaw("{\"seq\":" + seq + ",\"type\":\"request\",\"command\":\"" + command + "\""
            + (argsJson is null ? "" : ",\"arguments\":" + argsJson) + "}");
        return seq;
    }

    public JsonElement WaitResponse(int requestSeq, int timeoutMs = 30000)
        => Take(
            d => d.RootElement.GetProperty("type").GetString() == "response"
                && d.RootElement.TryGetProperty("request_seq", out JsonElement rs)
                && rs.GetInt32() == requestSeq,
            "response to request " + requestSeq, timeoutMs).RootElement;

    public JsonElement WaitEvent(string name, int timeoutMs = 15000)
        => Take(
            d => d.RootElement.GetProperty("type").GetString() == "event"
                && d.RootElement.GetProperty("event").GetString() == name,
            "event '" + name + "'", timeoutMs).RootElement;

    /// <summary>A REVERSE request — the adapter asking us, VS Code, to do something
    /// (runInTerminal). The caller answers with <see cref="SendRaw"/>.</summary>
    public JsonElement WaitReverseRequest(string command, int timeoutMs = 15000)
        => Take(
            d => d.RootElement.GetProperty("type").GetString() == "request"
                && d.RootElement.GetProperty("command").GetString() == command,
            "reverse request '" + command + "'", timeoutMs).RootElement;

    public void SendRaw(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] header = Encoding.ASCII.GetBytes(
            "Content-Length: " + body.Length + "\r\n\r\n");
        lock (_out)
        {
            _out.Write(header, 0, header.Length);
            _out.Write(body, 0, body.Length);
            _out.Flush();
        }
    }

    private JsonDocument Take(
        Func<JsonDocument, bool> match, string what, int timeoutMs = 15000)
    {
        for (int i = 0; i < _stashed.Count; i++)
        {
            if (match(_stashed[i]))
            {
                JsonDocument found = _stashed[i];
                _stashed.RemoveAt(i);
                return found;
            }
        }
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            if (!_incoming.TryTake(out JsonDocument? doc, remaining))
                throw new TimeoutException("no " + what + " within " + timeoutMs + "ms");
            if (match(doc)) return doc;
            _stashed.Add(doc);
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (!_disposed)
            {
                JsonDocument? doc = ReadMessage(_in);
                if (doc is null) break;
                _incoming.Add(doc);
            }
        }
        catch (Exception) { /* closed */ }
    }

    private static JsonDocument? ReadMessage(Stream stream)
    {
        int length = -1;
        var line = new StringBuilder();
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0) return null;
            if (b == '\n')
            {
                string l = line.ToString().TrimEnd('\r');
                line.Clear();
                if (l.Length == 0) break;
                if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(l.Substring("Content-Length:".Length).Trim());
            }
            else line.Append((char)b);
        }
        if (length < 0) return null;
        byte[] body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = stream.Read(body, read, length - read);
            if (n <= 0) return null;
            read += n;
        }
        return JsonDocument.Parse(body);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _tcp?.Close(); } catch (Exception) { }
        try { _in.Dispose(); } catch (Exception) { }
        try { _out.Dispose(); } catch (Exception) { }
    }
}
