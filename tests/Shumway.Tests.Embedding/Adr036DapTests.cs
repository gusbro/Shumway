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
using Shumway.Embedding.Debugging;
using Shumway.Embedding.Debugging.Dap;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-036 V1 — the in-process DAP server, driven exactly as VS Code drives it:
/// a real TCP client speaking framed DAP JSON against the real server, cross-platform,
/// no IDE in the loop. The debuggee programs mirror the ADR-035 test corpus.</summary>
[Collection("debugger")]
public class Adr036DapTests
{
    private readonly ITestOutputHelper _log;
    public Adr036DapTests(ITestOutputHelper log) => _log = log;

    //  2: :- dynamic(log/1).
    //  3: run(Out) :-
    //  4:     step(X),
    //  5:     note(X),
    //  6:     Out = X.
    //  7: step(1).
    //  8: note(T) :- assertz(log(T)).
    private const string Program =
        ":- dynamic(log/1).\n" +
        "run(Out) :-\n    step(X),\n    note(X),\n    Out = X.\n" +
        "step(1).\nnote(T) :- assertz(log(T)).\n";

    /// <summary>A debug-compiled engine + session + DAP server on an ephemeral port —
    /// the shape the REPL's <c>--debug --dap</c> will wire in V5.</summary>
    private static (PrologEngine Engine, ChannelDebugSession Session, DapDebugServer Server)
        StartDebuggee(string? program = null)
    {
        var engine = new PrologEngine();
        var session = new ChannelDebugSession(engine);
        engine.ConsultString(
            ":- set_prolog_flag(compile_mode, debug).\n" + (program ?? Program));
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        var server = new DapDebugServer(session, port: 0);
        return (engine, session, server);
    }

    [Fact]
    public void Initialize_Handshake_CapabilitiesAndInitializedEvent()
    {
        var (_, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            JsonElement response = client.Request("initialize");
            Assert.True(response.GetProperty("success").GetBoolean());
            Assert.True(response.GetProperty("body")
                .GetProperty("supportsConditionalBreakpoints").GetBoolean());
            client.WaitEvent("initialized");
        }
    }

    [Fact]
    public void Breakpoint_Stops_StackAndVariables_ThenContinues()
    {
        var (engine, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            client.Request("initialize");
            client.WaitEvent("initialized");
            JsonElement bps = client.Request("setBreakpoints",
                "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":[{\"line\":5}]}");
            Assert.True(bps.GetProperty("body").GetProperty("breakpoints")[0]
                .GetProperty("verified").GetBoolean());
            client.Request("configurationDone");

            var query = new QueryRun(engine, "run(Out).");

            JsonElement stopped = client.WaitEvent("stopped");
            Assert.Equal("breakpoint", stopped.GetProperty("body")
                .GetProperty("reason").GetString());

            // The stack, from the snapshot the engine wrote before blocking.
            JsonElement stack = client.Request("stackTrace", "{\"threadId\":1}");
            JsonElement frames = stack.GetProperty("body").GetProperty("stackFrames");
            Assert.True(frames.GetArrayLength() >= 1);
            JsonElement top = frames[0];
            Assert.Equal(5, top.GetProperty("line").GetInt32());
            _log.WriteLine("top frame: " + top.GetProperty("name").GetString());

            // Locals of the top frame: X is bound to 1 by step(X) at line 5.
            int frameId = top.GetProperty("id").GetInt32();
            JsonElement scopes = client.Request("scopes", "{\"frameId\":" + frameId + "}");
            int varsRef = scopes.GetProperty("body").GetProperty("scopes")[0]
                .GetProperty("variablesReference").GetInt32();
            JsonElement vars = client.Request("variables",
                "{\"variablesReference\":" + varsRef + "}");
            var byName = vars.GetProperty("body").GetProperty("variables")
                .EnumerateArray()
                .ToDictionary(
                    v => v.GetProperty("name").GetString()!,
                    v => v.GetProperty("value").GetString()!);
            _log.WriteLine("locals: " + string.Join(", ", byName.Select(kv => kv.Key + "=" + kv.Value)));
            Assert.Equal("1", byName["X"]);

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
            Assert.Single(query.Solutions!);
        }
    }

    [Fact]
    public void ConditionalBreakpoint_StopsOnlyWhenTheGoalHolds()
    {
        //  2: :- dynamic(log/1).
        //  3: main :- loop(1).
        //  4: loop(N) :- N =< 5, note(N), N1 is N + 1, loop(N1).
        //  5: loop(N) :- N > 5.
        //  6: note(T) :- assertz(log(T)).
        var (engine, session, server) = StartDebuggee("""
            :- dynamic(log/1).
            main :- loop(1).
            loop(N) :- N =< 5, note(N), N1 is N + 1, loop(N1).
            loop(N) :- N > 5.
            note(T) :- assertz(log(T)).
            """);
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            client.Request("initialize");
            client.WaitEvent("initialized");
            client.Request("setBreakpoints",
                "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":"
                + "[{\"line\":6,\"condition\":\"T =:= 3\"}]}");
            client.Request("configurationDone");

            var query = new QueryRun(engine, "main.");

            client.WaitEvent("stopped");
            JsonElement stack = client.Request("stackTrace", "{\"threadId\":1}");
            int frameId = stack.GetProperty("body").GetProperty("stackFrames")[0]
                .GetProperty("id").GetInt32();
            JsonElement vars = client.Request("variables",
                "{\"variablesReference\":" + frameId + "}");
            string t = vars.GetProperty("body").GetProperty("variables")
                .EnumerateArray()
                .Single(v => v.GetProperty("name").GetString() == "T")
                .GetProperty("value").GetString()!;
            Assert.Equal("3", t);   // T = 1 and 2 ran past the condition silently

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
            Assert.Single(query.Solutions!);
        }
    }

    [Fact]
    public void StepOver_StopsAgain_AtAStepPort()
    {
        var (engine, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            client.Request("initialize");
            client.WaitEvent("initialized");
            client.Request("setBreakpoints",
                "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":[{\"line\":4}]}");
            client.Request("configurationDone");

            var query = new QueryRun(engine, "run(Out).");
            client.WaitEvent("stopped");

            client.Request("next", "{\"threadId\":1}");
            JsonElement stopped = client.WaitEvent("stopped");
            Assert.Equal("step", stopped.GetProperty("body").GetProperty("reason").GetString());
            JsonElement stack = client.Request("stackTrace", "{\"threadId\":1}");
            int line = stack.GetProperty("body").GetProperty("stackFrames")[0]
                .GetProperty("line").GetInt32();
            _log.WriteLine("after next: line " + line);
            Assert.InRange(line, 4, 6);   // the exit of step(X) / the next goal

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
        }
    }

    [Fact]
    public void Pause_StopsARunningQuery_AtAPort()
    {
        // 60k debug-mode iterations ≈ seconds of runway; the pause lands ~250 ms
        // in (Sleep(200) + socket round-trip), leaving ~5/6 of the loop still to
        // run — same guarantee as the original 300k at a fifth of the wall time.
        var (engine, session, server) = StartDebuggee("""
            loop :- between(1, 60000, I), tick(I), fail.
            loop.
            tick(_).
            """);
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            client.Request("initialize");
            client.WaitEvent("initialized");
            client.Request("configurationDone");

            var query = new QueryRun(engine, "loop.");
            Thread.Sleep(200);   // let it get going

            client.Request("pause", "{\"threadId\":1}");
            JsonElement stopped = client.WaitEvent("stopped");
            Assert.Equal("pause", stopped.GetProperty("body").GetProperty("reason").GetString());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(60_000), "query must complete");
            Assert.Single(query.Solutions!);
        }
    }

    [Fact]
    public void WaitForDapConfigured_Releases_OnConfigurationDone()
    {
        // The --dap-wait gate: the program's start blocks until the client's
        // breakpoints are ARMED — the fix for the launch race where a goal typed in
        // the first second ran past every breakpoint.
        var (_, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            Assert.False(session.WaitForDapConfigured(TimeSpan.FromMilliseconds(100)),
                "must not release before the client configures");

            client.Request("initialize");
            client.WaitEvent("initialized");
            client.Request("setBreakpoints",
                "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":[{\"line\":5}]}");
            client.Request("configurationDone");

            Assert.True(session.WaitForDapConfigured(TimeSpan.FromSeconds(5)),
                "configurationDone must release the gate");
        }
    }

    [Fact]
    public void SecondClient_IsRefused_WhileTheFirstDrives()
    {
        var (_, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var first = new DapTestClient(server.Port))
        {
            first.Request("initialize");
            first.WaitEvent("initialized");

            using var second = new DapTestClient(server.Port);
            JsonElement refused = second.Request("initialize", expectSuccess: false);
            Assert.False(refused.GetProperty("success").GetBoolean());
            Assert.Contains("already attached", refused.GetProperty("message").GetString());
        }
    }

    [Fact]
    public void Disconnect_ClearsBreakpoints_AndTheProgramRunsFree()
    {
        var (engine, session, server) = StartDebuggee();
        using (session)
        using (server)
        {
            using (var client = new DapTestClient(server.Port))
            {
                client.Request("initialize");
                client.WaitEvent("initialized");
                client.Request("setBreakpoints",
                    "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":[{\"line\":5}]}");
                client.Request("configurationDone");
                client.Request("disconnect");
            }

            // No client: the query must run to completion without stopping anywhere.
            var query = new QueryRun(engine, "run(Out).");
            Assert.True(query.Join(20_000),
                "the program must run free after a disconnect");
            Assert.Single(query.Solutions!);
        }
    }

    [Fact]
    public void Reconnect_AfterDisconnect_DebugsAgain()
    {
        var (engine, session, server) = StartDebuggee();
        using (session)
        using (server)
        {
            using (var client = new DapTestClient(server.Port))
            {
                client.Request("initialize");
                client.WaitEvent("initialized");
                client.Request("disconnect");
            }

            // The seat is free again: a second client connects and a breakpoint works.
            Thread.Sleep(200);   // let the server unwire the first connection
            using (var client = new DapTestClient(server.Port))
            {
                client.Request("initialize");
                client.WaitEvent("initialized");
                client.Request("setBreakpoints",
                    "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":[{\"line\":5}]}");
                client.Request("configurationDone");

                var query = new QueryRun(engine, "run(Out).");
                client.WaitEvent("stopped");
                client.Request("continue", "{\"threadId\":1}");
                Assert.True(query.Join(20_000), "query must complete");
            }
        }
    }

    [Fact]
    public void StackTrace_WhileRunning_IsEmpty_NotStale()
    {
        var (engine, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            client.Request("initialize");
            client.WaitEvent("initialized");
            client.Request("setBreakpoints",
                "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":[{\"line\":5}]}");
            client.Request("configurationDone");

            var query = new QueryRun(engine, "run(Out).");
            client.WaitEvent("stopped");
            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000));

            // The stop is over: its stack is history and must not be shown as current.
            JsonElement stack = client.Request("stackTrace", "{\"threadId\":1}");
            Assert.Equal(0, stack.GetProperty("body")
                .GetProperty("stackFrames").GetArrayLength());
        }
    }
}
