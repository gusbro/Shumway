using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Shumway.Embedding.Debugging.Dap;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-036 V2 — the <c>shumway-dap</c> adapter (<see cref="DapProxy"/>), driven
/// as VS Code drives it: the test plays the IDE on the two halves of the adapter's
/// stdio, and a real engine + <see cref="DapDebugServer"/> plays the debuggee.</summary>
[Collection("debugger")]
public class Adr036ProxyTests
{
    private const string Program =
        ":- dynamic(log/1).\n" +
        "run(Out) :-\n    step(X),\n    note(X),\n    Out = X.\n" +
        "step(1).\nnote(T) :- assertz(log(T)).\n";

    private static (PrologEngine Engine, ChannelDebugSession Session, DapDebugServer Server)
        StartDebuggee()
    {
        var engine = new PrologEngine();
        var session = new ChannelDebugSession(engine);
        engine.ConsultString(
            ":- set_prolog_flag(compile_mode, debug).\n" + Program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        var server = new DapDebugServer(session, port: 0);
        return (engine, session, server);
    }

    /// <summary>The adapter on in-process pipes: what VS Code writes to its stdin, what
    /// it reads from its stdout, and the Run loop on its own thread.</summary>
    private sealed class ProxyHarness : IDisposable
    {
        private readonly AnonymousPipeServerStream _toProxyWrite;
        private readonly AnonymousPipeClientStream _toProxyRead;
        private readonly AnonymousPipeServerStream _fromProxyWrite;
        private readonly AnonymousPipeClientStream _fromProxyRead;
        private readonly Thread _thread;
        public readonly DapTestClient Client;

        public ProxyHarness()
        {
            _toProxyWrite = new AnonymousPipeServerStream(PipeDirection.Out);
            _toProxyRead = new AnonymousPipeClientStream(
                PipeDirection.In, _toProxyWrite.ClientSafePipeHandle);
            _fromProxyWrite = new AnonymousPipeServerStream(PipeDirection.Out);
            _fromProxyRead = new AnonymousPipeClientStream(
                PipeDirection.In, _fromProxyWrite.ClientSafePipeHandle);

            var proxy = new DapProxy(_toProxyRead, _fromProxyWrite);
            _thread = new Thread(proxy.Run) { IsBackground = true, Name = "dap-test-proxy" };
            _thread.Start();

            Client = new DapTestClient(_fromProxyRead, _toProxyWrite);
        }

        public void Dispose()
        {
            Client.Dispose();
            _thread.Join(2000);
            foreach (IDisposable d in new IDisposable[]
                { _toProxyWrite, _toProxyRead, _fromProxyWrite, _fromProxyRead })
                try { d.Dispose(); } catch (Exception) { }
        }
    }

    [Fact]
    public void Attach_ThroughTheProxy_FullBreakpointRound()
    {
        var (engine, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var harness = new ProxyHarness())
        {
            DapTestClient client = harness.Client;

            // initialize is answered by the ADAPTER (no debuggee yet).
            JsonElement init = client.Request("initialize");
            Assert.True(init.GetProperty("body")
                .GetProperty("supportsConditionalBreakpoints").GetBoolean());

            // attach connects it to the real server; the server's initialized event
            // flows through to us.
            client.Request("attach", "{\"port\":" + server.Port + "}");
            client.WaitEvent("initialized");

            // From here everything is verbatim forwarding, both directions.
            client.Request("setBreakpoints",
                "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":[{\"line\":5}]}");
            client.Request("configurationDone");

            var query = new QueryRun(engine, "run(Out).");
            JsonElement stopped = client.WaitEvent("stopped");
            Assert.Equal("breakpoint",
                stopped.GetProperty("body").GetProperty("reason").GetString());

            JsonElement stack = client.Request("stackTrace", "{\"threadId\":1}");
            Assert.Equal(5, stack.GetProperty("body").GetProperty("stackFrames")[0]
                .GetProperty("line").GetInt32());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
            Assert.Single(query.Solutions!);
        }
    }

    [Fact]
    public void Launch_RunInTerminalHandshake_ThenDebugs()
    {
        // The launch flow, with the test playing BOTH ends: VS Code (answer the
        // runInTerminal reverse request) and the terminal (the "launched" debuggee is a
        // server this test starts on the port the adapter chose).
        PrologEngine? engine = null;
        ChannelDebugSession? sessionOut = null;
        DapDebugServer? serverOut = null;
        try
        {
            using var harness = new ProxyHarness();
            DapTestClient client = harness.Client;

            client.Request("initialize");
            int launchSeq = client.RequestAsync("launch",
                "{\"program\":\"app.pl\",\"shumwayPath\":\"shumway\"}");

            // The adapter asks its client to run the debuggee in the terminal. The
            // command line carries the port it picked.
            JsonElement rit = client.WaitReverseRequest("runInTerminal");
            var args = rit.GetProperty("arguments").GetProperty("args")
                .EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Equal("shumway", args[0]);
            Assert.Equal("--dap-wait", args[1]);
            int port = int.Parse(args[2]);
            Assert.Contains("app.pl", args);

            // "The terminal runs it": the debuggee comes up on that port...
            (engine, sessionOut, serverOut) = StartDebuggeeOn(port);
            // ...and VS Code answers the reverse request.
            client.SendRaw("{\"seq\":1,\"type\":\"response\",\"request_seq\":"
                + rit.GetProperty("seq").GetInt32()
                + ",\"command\":\"runInTerminal\",\"success\":true,\"body\":{}}");

            // The adapter connects, the backend's initialized flows through, and the
            // launch is answered.
            client.WaitEvent("initialized");
            Assert.True(client.WaitResponse(launchSeq)
                .GetProperty("success").GetBoolean());

            // And it is a real debug session end to end.
            client.Request("setBreakpoints",
                "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":[{\"line\":5}]}");
            client.Request("configurationDone");
            var query = new QueryRun(engine, "run(Out).");
            client.WaitEvent("stopped");
            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
        }
        finally
        {
            serverOut?.Dispose();
            sessionOut?.Dispose();
        }
    }

    private static (PrologEngine, ChannelDebugSession, DapDebugServer) StartDebuggeeOn(
        int port)
    {
        var engine = new PrologEngine();
        var session = new ChannelDebugSession(engine);
        engine.ConsultString(
            ":- set_prolog_flag(compile_mode, debug).\n" + Program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        var server = new DapDebugServer(session, port);
        return (engine, session, server);
    }
}
