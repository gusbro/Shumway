using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Shumway.Embedding.Debugging.Dap;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-036 V3 — Debug Console evaluation (the Immediate window over DAP) and
/// the destructive setVariable, against the real server over the real socket.</summary>
public class Adr036EvalTests
{
    private readonly ITestOutputHelper _log;
    public Adr036EvalTests(ITestOutputHelper log) => _log = log;

    //  2: :- dynamic(log/1).
    //  3: run(Out) :-
    //  4:     probe(X),
    //  5:     emit(X),
    //  6:     Out = X.
    //  7: probe(_).
    //  8: emit(X) :- ( nonvar(X) -> assertz(log(X)) ; true ).
    private const string Program =
        ":- dynamic(log/1).\n" +
        "run(Out) :-\n    probe(X),\n    emit(X),\n    Out = X.\n" +
        "probe(_).\nemit(X) :- ( nonvar(X) -> assertz(log(X)) ; true ).\n";

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

    /// <summary>Attach, arm a breakpoint at line 5 (X still free), start the query, and
    /// hand back the stopped client.</summary>
    private static QueryRun StopAtLine5(PrologEngine engine, DapTestClient client)
    {
        client.Request("initialize");
        client.WaitEvent("initialized");
        client.Request("setBreakpoints",
            "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":[{\"line\":5}]}");
        client.Request("configurationDone");
        var query = new QueryRun(engine, "run(Out).");
        client.WaitEvent("stopped");
        return query;
    }

    private static Dictionary<string, string> Variables(DapTestClient client, int frameId)
        => client.Request("variables", "{\"variablesReference\":" + frameId + "}")
            .GetProperty("body").GetProperty("variables").EnumerateArray()
            .ToDictionary(
                v => v.GetProperty("name").GetString()!,
                v => v.GetProperty("value").GetString()!);

    [Fact]
    public void DebugConsole_RunsAGoal_InTheLiveEngine()
    {
        var (engine, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            QueryRun query = StopAtLine5(engine, client);

            // A side-effecting goal, in the LIVE engine: the assert persists.
            JsonElement eval = client.Request("evaluate",
                "{\"expression\":\"assertz(log(desde_consola))\","
                + "\"frameId\":1,\"context\":\"repl\"}");
            _log.WriteLine("eval: " + eval.GetProperty("body")
                .GetProperty("result").GetString());
            Assert.Contains("true", eval.GetProperty("body")
                .GetProperty("result").GetString());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
            Assert.Single(engine.QueryAll("log(desde_consola).").ToList());
        }
    }

    [Fact]
    public void DebugConsole_BindIntoFrame_ReachesTheProgram()
    {
        var (engine, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            QueryRun query = StopAtLine5(engine, client);

            // Bind the frame's free X from the console — ADR-035's bind-into-frame: the
            // program itself then runs with it (emit asserts log(inyectado(9))).
            JsonElement eval = client.Request("evaluate",
                "{\"expression\":\"X = inyectado(9)\",\"frameId\":1,\"context\":\"repl\"}");
            string result = eval.GetProperty("body").GetProperty("result").GetString()!;
            _log.WriteLine("eval: " + result);
            Assert.Contains("committed to the frame", result);

            // The invalidated event told the client to refresh; the refreshed Locals
            // show the binding.
            client.WaitEvent("invalidated");
            Assert.Equal("inyectado(9)", Variables(client, 1)["X"]);

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
            Assert.Single(engine.QueryAll("log(inyectado(9)).").ToList());
        }
    }

    [Fact]
    public void DebugConsole_Semicolon_PumpsTheNextSolution()
    {
        var (engine, session, server) = StartDebuggee(
            ":- dynamic(log/1).\n" +
            "run(Out) :-\n    probe(X),\n    emit(X),\n    Out = X.\n" +
            "probe(_).\nemit(_).\n" +
            "color(rojo).\ncolor(verde).\n");
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            QueryRun query = StopAtLine5(engine, client);

            JsonElement first = client.Request("evaluate",
                "{\"expression\":\"color(C)\",\"frameId\":1,\"context\":\"repl\"}");
            Assert.Contains("rojo",
                first.GetProperty("body").GetProperty("result").GetString());

            JsonElement second = client.Request("evaluate",
                "{\"expression\":\";\",\"frameId\":1,\"context\":\"repl\"}");
            Assert.Contains("verde",
                second.GetProperty("body").GetProperty("result").GetString());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
        }
    }

    [Fact]
    public void Evaluate_SuppressesNestedBreakpoints_NoDeadlock()
    {
        var (engine, session, server) = StartDebuggee(
            ":- dynamic(log/1).\n" +
            "run(Out) :-\n    probe(X),\n    emit(X),\n    Out = X.\n" +
            "probe(_).\nemit(_).\n" +
            "helper(N) :- mark(N).\n" +
            "mark(N) :- assertz(log(N)).\n");
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            QueryRun query = StopAtLine5(engine, client);

            // A breakpoint INSIDE the code the console goal calls (line 10, mark's
            // clause — reached only via helper). The evaluation must run straight
            // through it: a nested stop routed to the reader thread would deadlock
            // against the parked engine thread's gate.
            client.Request("setBreakpoints",
                "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":"
                + "[{\"line\":5},{\"line\":10}]}");
            JsonElement eval = client.Request("evaluate",
                "{\"expression\":\"helper(77)\",\"frameId\":1,\"context\":\"repl\"}");
            Assert.Contains("true",
                eval.GetProperty("body").GetProperty("result").GetString());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
            Assert.Single(engine.QueryAll("log(77).").ToList());
        }
    }

    [Fact]
    public void DebugConsole_BareVariableName_PrintsItsValue()
    {
        //  X is BOUND at the stop (step(X) bound 1 before line 5).
        var (engine, session, server) = StartDebuggee(
            ":- dynamic(log/1).\n" +
            "run(Out) :-\n    step(X),\n    emit(X),\n    Out = X.\n" +
            "step(1).\nemit(_).\n");
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            QueryRun query = StopAtLine5(engine, client);

            // The Visual Studio Immediate behaviour: a bare variable name answers its
            // value — it is a question, not a goal.
            JsonElement bound = client.Request("evaluate",
                "{\"expression\":\"X\",\"frameId\":1,\"context\":\"repl\"}");
            Assert.Equal("1", bound.GetProperty("body").GetProperty("result").GetString());

            JsonElement free = client.Request("evaluate",
                "{\"expression\":\"Out\",\"frameId\":1,\"context\":\"repl\"}");
            Assert.StartsWith("_", free.GetProperty("body")
                .GetProperty("result").GetString());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
        }
    }

    [Fact]
    public void Hover_AnswersVariables_RefusesGoals()
    {
        var (engine, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            QueryRun query = StopAtLine5(engine, client);

            // A frame variable: answered from the snapshot, nothing runs.
            JsonElement hover = client.Request("evaluate",
                "{\"expression\":\"Out\",\"frameId\":1,\"context\":\"hover\"}");
            Assert.StartsWith("_", hover.GetProperty("body")
                .GetProperty("result").GetString());   // Out is still free

            // A predicate name: REFUSED — hovering `run` must not run run/1 (the
            // DataTip lesson, ADR-035).
            JsonElement refused = client.Request("evaluate",
                "{\"expression\":\"emit(1)\",\"frameId\":1,\"context\":\"hover\"}",
                expectSuccess: false);
            Assert.False(refused.GetProperty("success").GetBoolean());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
        }
    }

    [Fact]
    public void SetVariable_Rebinds_AndBacktrackingRestores()
    {
        var (engine, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            QueryRun query = StopAtLine5(engine, client);

            JsonElement set = client.Request("setVariable",
                "{\"variablesReference\":1,\"name\":\"X\",\"value\":\"editado(3)\"}");
            Assert.Equal("editado(3)", set.GetProperty("body")
                .GetProperty("value").GetString());
            Assert.Equal("editado(3)", Variables(client, 1)["X"]);

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
            // The program ran on with the edit: emit asserted it.
            Assert.Single(engine.QueryAll("log(editado(3)).").ToList());
        }
    }

    [Fact]
    public void SetVariable_Underscore_Uninstantiates()
    {
        //  program with X BOUND at the stop: step(X) binds 1, bp at line 5.
        var (engine, session, server) = StartDebuggee(
            ":- dynamic(log/1).\n" +
            "run(Out) :-\n    step(X),\n    emit(X),\n    Out = X.\n" +
            "step(1).\nemit(X) :- ( nonvar(X) -> assertz(log(X)) ; true ).\n");
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            QueryRun query = StopAtLine5(engine, client);
            Assert.Equal("1", Variables(client, 1)["X"]);

            client.Request("setVariable",
                "{\"variablesReference\":1,\"name\":\"X\",\"value\":\"_\"}");
            Assert.StartsWith("_", Variables(client, 1)["X"]);   // free again

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
            // X arrived at emit UNBOUND: nothing was asserted.
            Assert.Empty(engine.QueryAll("log(V).").ToList());
        }
    }

    [Fact]
    public void SetVariable_WriteqValue_RoundTrips()
    {
        var (engine, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            QueryRun query = StopAtLine5(engine, client);

            // A quoted atom that LOOKS like a number: the response must render writeq
            // ('1234' with quotes), or a copy-paste edit would silently change its type.
            JsonElement set = client.Request("setVariable",
                "{\"variablesReference\":1,\"name\":\"X\",\"value\":\"hola('1234')\"}");
            Assert.Equal("hola('1234')", set.GetProperty("body")
                .GetProperty("value").GetString());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
        }
    }

    [Fact]
    public void SetVariable_Refusal_IsAnHonestError()
    {
        var (engine, session, server) = StartDebuggee();
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            QueryRun query = StopAtLine5(engine, client);

            JsonElement refused = client.Request("setVariable",
                "{\"variablesReference\":1,\"name\":\"NoExiste\",\"value\":\"1\"}",
                expectSuccess: false);
            Assert.Contains("no variable",
                refused.GetProperty("message").GetString());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
        }
    }
}
