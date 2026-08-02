using System;
using System.Linq;
using System.Text.Json;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Shumway.Embedding.Debugging.Dap;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-036 — the Constraints scope: residual constraints of a frame's attributed
/// variables over DAP. A frame with attributed variables serves a second scope next to
/// Locals; one without does not.
/// </summary>
public class Adr036ResidualTests
{
    private readonly ITestOutputHelper _log;

    public Adr036ResidualTests(ITestOutputHelper log) => _log = log;

    //  2: :- use_module(library(clpfd)).
    //  3: p(X, Y) :-
    //  4:     X in 1..9, X #< Y, Y in 3..7,
    //  5:     mark(X, Y).
    //  6: mark(_, _).
    private const string Program =
        ":- use_module(library(clpfd)).\n" +
        "p(X, Y) :-\n    X in 1..9, X #< Y, Y in 3..7,\n    mark(X, Y).\nmark(_, _).\n";

    private static (PrologEngine Engine, ChannelDebugSession Session, DapDebugServer Server)
        StartDebuggee()
    {
        var engine = new PrologEngine();
        var session = new ChannelDebugSession(engine);
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + Program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        var server = new DapDebugServer(session, port: 0);
        return (engine, session, server);
    }

    [Fact]
    public void TheConstraintsScopeServesTheResidualRows()
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

            var query = new QueryRun(engine, "p(A, B).");
            client.WaitEvent("stopped");

            JsonElement stack = client.Request("stackTrace", "{\"threadId\":1}");
            int frameId = stack.GetProperty("body").GetProperty("stackFrames")[0]
                .GetProperty("id").GetInt32();

            JsonElement scopes = client.Request("scopes", "{\"frameId\":" + frameId + "}");
            var scopeList = scopes.GetProperty("body").GetProperty("scopes")
                .EnumerateArray().ToList();
            _log.WriteLine("scopes: " + string.Join(", ",
                scopeList.Select(s => s.GetProperty("name").GetString())));
            Assert.Contains(scopeList, s => s.GetProperty("name").GetString() == "Constraints");

            int constraintsRef = scopeList
                .First(s => s.GetProperty("name").GetString() == "Constraints")
                .GetProperty("variablesReference").GetInt32();
            JsonElement vars = client.Request("variables",
                "{\"variablesReference\":" + constraintsRef + "}");
            var byName = vars.GetProperty("body").GetProperty("variables")
                .EnumerateArray()
                .ToDictionary(
                    v => v.GetProperty("name").GetString()!,
                    v => v.GetProperty("value").GetString()!);
            _log.WriteLine("constraints: "
                + string.Join("; ", byName.Select(kv => kv.Key + " = " + kv.Value)));
            Assert.Contains("in", byName["X"]);
            Assert.Contains("#<", byName["X"]);
            Assert.Contains("in", byName["Y"]);

            // Constraints are read-only over DAP.
            JsonElement refused = client.Request("setVariable",
                "{\"variablesReference\":" + constraintsRef
                + ",\"name\":\"X\",\"value\":\"9\"}", expectSuccess: false);
            Assert.False(refused.GetProperty("success").GetBoolean());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
        }
    }

    [Fact]
    public void AFrameWithoutAttributedVariablesHasNoConstraintsScope()
    {
        var engine = new PrologEngine();
        var session = new ChannelDebugSession(engine);
        engine.ConsultString(
            ":- set_prolog_flag(compile_mode, debug).\n"
            + "q(X) :-\n    X = plain,\n    mark(X).\nmark(_).\n");
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        var server = new DapDebugServer(session, port: 0);
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            client.Request("initialize");
            client.WaitEvent("initialized");
            client.Request("setBreakpoints",
                "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":[{\"line\":3}]}");
            client.Request("configurationDone");

            var query = new QueryRun(engine, "q(A).");
            client.WaitEvent("stopped");

            JsonElement stack = client.Request("stackTrace", "{\"threadId\":1}");
            int frameId = stack.GetProperty("body").GetProperty("stackFrames")[0]
                .GetProperty("id").GetInt32();
            JsonElement scopes = client.Request("scopes", "{\"frameId\":" + frameId + "}");
            Assert.DoesNotContain(
                scopes.GetProperty("body").GetProperty("scopes").EnumerateArray(),
                s => s.GetProperty("name").GetString() == "Constraints");

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
        }
    }
}
