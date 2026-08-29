using System;
using System.Linq;
using System.Text.Json;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Shumway.Embedding.Debugging.Dap;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-036 V4 — Jump to Cursor over DAP: gotoTargets answers from the engine's
/// published SetNextLines; goto runs the ADR-035 Set Next Statement (forward skip,
/// backward trail rewind, cross-frame via the Call Stack selection).</summary>
[Collection("debugger")]
public class Adr036GotoTests
{
    private readonly ITestOutputHelper _log;
    public Adr036GotoTests(ITestOutputHelper log) => _log = log;

    //  2: :- dynamic(log/1).
    //  3: run :-
    //  4:     mark(a),
    //  5:     mark(b),
    //  6:     mark(c).
    //  7: mark(X) :- assertz(log(X)).
    private const string Marks =
        ":- dynamic(log/1).\n" +
        "run :-\n    mark(a),\n    mark(b),\n    mark(c).\n" +
        "mark(X) :- assertz(log(X)).\n";

    private static (PrologEngine Engine, ChannelDebugSession Session, DapDebugServer Server)
        StartDebuggee(string program)
    {
        var engine = new PrologEngine();
        var session = new ChannelDebugSession(engine);
        engine.ConsultString(
            ":- set_prolog_flag(compile_mode, debug).\n" + program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        var server = new DapDebugServer(session, port: 0);
        return (engine, session, server);
    }

    private static QueryRun StopAt(
        PrologEngine engine, DapTestClient client, int bpLine, string goal)
    {
        client.Request("initialize");
        client.WaitEvent("initialized");
        client.Request("setBreakpoints",
            "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":[{\"line\":" + bpLine + "}]}");
        client.Request("configurationDone");
        var query = new QueryRun(engine, goal);
        client.WaitEvent("stopped");
        return query;
    }

    private static string[] LoggedAtoms(PrologEngine engine)
        => engine.QueryAll("log(V).")
            .Select(s => s["V"]!.ToString()!)
            .ToArray();

    [Fact]
    public void GotoTargets_AnswerTheEnginesValidLines()
    {
        var (engine, session, server) = StartDebuggee(Marks);
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            QueryRun query = StopAt(engine, client, 4, "run.");

            // A statement of the clause: one target.
            JsonElement valid = client.Request("gotoTargets",
                "{\"source\":{\"path\":\"<string>\"},\"line\":6}");
            Assert.Equal(1, valid.GetProperty("body")
                .GetProperty("targets").GetArrayLength());

            // A line with nothing to stand on: empty — the editor greys the action.
            JsonElement invalid = client.Request("gotoTargets",
                "{\"source\":{\"path\":\"<string>\"},\"line\":2}");
            Assert.Equal(0, invalid.GetProperty("body")
                .GetProperty("targets").GetArrayLength());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
        }
    }

    [Fact]
    public void Goto_Forward_SkipsTheGoalsInBetween()
    {
        var (engine, session, server) = StartDebuggee(Marks);
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            // Stopped at line 4, BEFORE mark(a): jump to line 6 skips mark(a) and
            // mark(b) entirely.
            QueryRun query = StopAt(engine, client, 4, "run.");

            JsonElement targets = client.Request("gotoTargets",
                "{\"source\":{\"path\":\"<string>\"},\"line\":6}");
            int id = targets.GetProperty("body").GetProperty("targets")[0]
                .GetProperty("id").GetInt32();

            client.Request("goto", "{\"threadId\":1,\"targetId\":" + id + "}");
            JsonElement stopped = client.WaitEvent("stopped");
            Assert.Equal("goto", stopped.GetProperty("body")
                .GetProperty("reason").GetString());

            // The re-read stack already stands on the new line.
            JsonElement stack = client.Request("stackTrace", "{\"threadId\":1}");
            Assert.Equal(6, stack.GetProperty("body").GetProperty("stackFrames")[0]
                .GetProperty("line").GetInt32());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
            Assert.Equal(new[] { "c" }, LoggedAtoms(engine));
        }
    }

    [Fact]
    public void Goto_Backward_ReRunsTheGoal()
    {
        var (engine, session, server) = StartDebuggee(Marks);
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            // Stopped at line 6: mark(a) and mark(b) have run. Jump BACK to line 5 —
            // the trail rewinds to the recorded port mark — and continue: mark(b) runs
            // again (asserts are permanent, so log(b) ends up twice — the ADR-035
            // semantics, observable).
            QueryRun query = StopAt(engine, client, 6, "run.");

            JsonElement targets = client.Request("gotoTargets",
                "{\"source\":{\"path\":\"<string>\"},\"line\":5}");
            int id = targets.GetProperty("body").GetProperty("targets")[0]
                .GetProperty("id").GetInt32();
            client.Request("goto", "{\"threadId\":1,\"targetId\":" + id + "}");
            client.WaitEvent("stopped");

            client.Request("continue", "{\"threadId\":1}");
            // Re-running mark(b) arrives at line 6 again — where THIS test's breakpoint
            // is still armed. It fires again, correctly (the SNS one-shot suppression
            // covers only the line moved TO).
            client.WaitEvent("stopped");
            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
            string[] logged = LoggedAtoms(engine);
            _log.WriteLine("logged: " + string.Join(",", logged));
            Assert.Equal(new[] { "a", "b", "b", "c" }, logged);   // assertz order
        }
    }

    [Fact]
    public void Goto_CrossFrame_ViaTheCallStackSelection()
    {
        //  2: :- dynamic(log/1).
        //  3: main :- outer.
        //  4: outer :-
        //  5:     inner,
        //  6:     mark(after_inner).
        //  7: inner :-
        //  8:     mark(in1),
        //  9:     mark(in2).
        // 10: mark(X) :- assertz(log(X)).
        var (engine, session, server) = StartDebuggee("""
            :- dynamic(log/1).
            main :- outer.
            outer :-
                inner,
                mark(after_inner).
            inner :-
                mark(in1),
                mark(in2).
            mark(X) :- assertz(log(X)).
            """);
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            // Stop inside inner, at line 9 (mark(in1) already ran).
            QueryRun query = StopAt(engine, client, 9, "main.");

            // Select OUTER's frame in the Call Stack — the scopes fetch is the signal —
            // then jump to its line 6: the move pops inner and lands outer there.
            JsonElement stack = client.Request("stackTrace", "{\"threadId\":1}");
            JsonElement frames = stack.GetProperty("body").GetProperty("stackFrames");
            int outerId = -1;
            foreach (JsonElement f in frames.EnumerateArray())
                if (f.GetProperty("name").GetString()!.StartsWith("outer"))
                    outerId = f.GetProperty("id").GetInt32();
            Assert.True(outerId > 0, "outer must be on the stack");
            client.Request("scopes", "{\"frameId\":" + outerId + "}");

            JsonElement targets = client.Request("gotoTargets",
                "{\"source\":{\"path\":\"<string>\"},\"line\":6}");
            Assert.Equal(1, targets.GetProperty("body")
                .GetProperty("targets").GetArrayLength());
            int id = targets.GetProperty("body").GetProperty("targets")[0]
                .GetProperty("id").GetInt32();
            client.Request("goto", "{\"threadId\":1,\"targetId\":" + id + "}");
            client.WaitEvent("stopped");

            // The re-captured stack: inner is GONE, outer stands at line 6.
            JsonElement after = client.Request("stackTrace", "{\"threadId\":1}");
            JsonElement top = after.GetProperty("body").GetProperty("stackFrames")[0];
            Assert.StartsWith("outer", top.GetProperty("name").GetString());
            Assert.Equal(6, top.GetProperty("line").GetInt32());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
            string[] logged = LoggedAtoms(engine);
            _log.WriteLine("logged: " + string.Join(",", logged));
            Assert.Contains("in1", logged);
            Assert.DoesNotContain("in2", logged);      // skipped by the cross-frame move
            Assert.Contains("after_inner", logged);
        }
    }

    [Fact]
    public void Logpoint_Prints_WithoutStopping()
    {
        // ADR-036 V5 — a breakpoint with a logMessage stops the MACHINE but never the
        // USER: an output event per hit, {Var} holes filled from the frame, and the
        // program runs to completion with no `stopped` event and no continue requests.
        var (engine, session, server) = StartDebuggee("""
            :- dynamic(log/1).
            run :- work(1), work(2), work(3).
            work(N) :- assertz(log(N)).
            """);
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            client.Request("initialize");
            client.WaitEvent("initialized");
            client.Request("setBreakpoints",
                "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":"
                + "[{\"line\":4,\"logMessage\":\"trabajando N={N}\"}]}");
            client.Request("configurationDone");

            var query = new QueryRun(engine, "run.");
            var outputs = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 3; i++)
                outputs.Add(client.WaitEvent("output").GetProperty("body")
                    .GetProperty("output").GetString()!.TrimEnd());
            _log.WriteLine(string.Join(" / ", outputs));

            Assert.True(query.Join(20_000), "query must complete without any continue");
            Assert.Equal(
                new[] { "trabajando N=1", "trabajando N=2", "trabajando N=3" }, outputs);
        }
    }

    [Fact]
    public void Logpoint_And_PlainBreakpoint_Coexist()
    {
        var (engine, session, server) = StartDebuggee(Marks);
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            client.Request("initialize");
            client.WaitEvent("initialized");
            // Line 5 logs; line 6 stops.
            client.Request("setBreakpoints",
                "{\"source\":{\"path\":\"<string>\"},\"breakpoints\":"
                + "[{\"line\":5,\"logMessage\":\"paso por b\"},{\"line\":6}]}");
            client.Request("configurationDone");

            var query = new QueryRun(engine, "run.");
            Assert.Equal("paso por b", client.WaitEvent("output").GetProperty("body")
                .GetProperty("output").GetString()!.TrimEnd());
            JsonElement stopped = client.WaitEvent("stopped");
            Assert.Equal("breakpoint",
                stopped.GetProperty("body").GetProperty("reason").GetString());
            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
        }
    }

    [Fact]
    public void Goto_StaleTarget_IsRefusedHonestly()
    {
        var (engine, session, server) = StartDebuggee(Marks);
        using (session)
        using (server)
        using (var client = new DapTestClient(server.Port))
        {
            QueryRun query = StopAt(engine, client, 4, "run.");

            JsonElement refused = client.Request("goto",
                "{\"threadId\":1,\"targetId\":99}", expectSuccess: false);
            Assert.Contains("no such goto target",
                refused.GetProperty("message").GetString());

            client.Request("continue", "{\"threadId\":1}");
            Assert.True(query.Join(20_000), "query must complete");
        }
    }
}
