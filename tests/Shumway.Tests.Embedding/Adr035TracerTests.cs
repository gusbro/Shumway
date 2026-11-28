using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 phase D1 — the four-port debug seam, exercised through its first
/// consumer: the <c>trace/0</c> tracer. These are the tests that keep the
/// engine-side debug core honest with no debugger in the loop.
/// </summary>
[Collection("debugger")]
public class Adr035TracerTests
{
    private readonly ITestOutputHelper _log;

    public Adr035TracerTests(ITestOutputHelper log) => _log = log;

    /// <summary>Runs <paramref name="goal"/> with the tracer attached and
    /// returns the port lines it printed.</summary>
    private List<string> Trace(string program, string goal, out int solutions)
    {
        var engine = new PrologEngine();
        engine.ConsultString(program);
        var sink = new StringWriter();
        engine.SetTracing(true, sink);

        solutions = engine.QueryAll(goal).Count();
        engine.SetTracing(false);

        var lines = sink.ToString()
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .ToList();
        foreach (string l in lines) _log.WriteLine(l);
        return lines;
    }

    [Fact]
    public void DeterministicCall_ReportsCallThenExit()
    {
        var lines = Trace("p(1).\np(2).\n", "p(1).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Equal(new[]
        {
            "   Call: (1) p(1)",
            "   Exit: (1) p(1)",
        }, lines);
    }

    [Fact]
    public void ExitPort_ShowsWhatTheGoalBound()
    {
        // The whole point of copying argument cells at the call port: the exit
        // line must show the binding, not the variable it was called with.
        var lines = Trace("p(1).\n", "p(X).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Contains(lines, l => l.StartsWith("   Call: (1) p(_"));
        Assert.Contains("   Exit: (1) p(1)", lines);
    }

    [Fact]
    public void Backtracking_ReportsRedoOnTheChoicePointOwner()
    {
        // Two solutions: the second one comes from a redo of p/1.
        var lines = Trace("p(1).\np(2).\n", "p(X).", out int solutions);

        Assert.Equal(2, solutions);
        Assert.Contains("   Exit: (1) p(1)", lines);
        Assert.Contains(lines, l => l.StartsWith("   Redo: (1) p("));
        Assert.Contains("   Exit: (1) p(2)", lines);
    }

    [Fact]
    public void FailingGoal_ReportsFailPort()
    {
        var lines = Trace("p(1).\n", "p(9).", out int solutions);

        Assert.Equal(0, solutions);
        Assert.Contains("   Call: (1) p(9)", lines);
        Assert.Contains("   Fail: (1) p(9)", lines);
    }

    [Fact]
    public void NestedCalls_Nest_AndFailurePopsTheWholeSubtree()
    {
        // q/1 calls r/1, which fails: the Fail lines must unwind both, deepest
        // first, and the depth column must reflect the real nesting.
        var lines = Trace("q(X) :- r(X), s(X).\nr(1).\n", "q(2).", out int solutions);

        Assert.Equal(0, solutions);
        Assert.Contains("   Call: (1) q(2)", lines);
        Assert.Contains("   Call: (2) r(2)", lines);
        Assert.Contains("   Fail: (2) r(2)", lines);
        Assert.Contains("   Fail: (1) q(2)", lines);
        // s/1 is never reached — r/1 failed first.
        Assert.DoesNotContain(lines, l => l.Contains(" s("));
    }

    [Fact]
    public void TailRecursion_DoesNotGrowTheTracedDepth()
    {
        // count/1's recursive call is a tail call: last-call optimisation reuses
        // the frame, so the traced depth must stay flat rather than climbing
        // once per iteration — that is what the tailCall flag on the call port
        // is for.
        var lines = Trace(
            "count(0) :- !.\ncount(N) :- M is N - 1, count(M).\n",
            "count(20).", out int solutions);

        Assert.Equal(1, solutions);
        var countCalls = lines.Where(l => l.Contains(" count(")).ToList();
        Assert.True(countCalls.Count >= 21, $"expected 21+ count/1 port lines, got {countCalls.Count}");
        int maxDepth = countCalls
            .Select(l => int.Parse(l.Split('(')[1].Split(')')[0]))
            .Max();
        Assert.True(maxDepth <= 2, $"tail recursion should not deepen the trace; max depth was {maxDepth}");
    }

    [Fact]
    public void BuiltinGoals_AreTraced_WithTheirBindings()
    {
        // Note is/2 would not show up: ADR-018 compiles arithmetic inline, so
        // there is no builtin dispatch to report. atom_length/2 is a real one.
        var lines = Trace("", "atom_length(abc, N).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Contains(lines, l => l.StartsWith("   Call: (1) atom_length(abc,"));
        Assert.Contains("   Exit: (1) atom_length(abc, 3)", lines);
    }

    [Theory]
    [InlineData(@"G = atom_length(abc, N), call(G).", 1, "   Exit: (1) atom_length(abc, 3)")]
    [InlineData(@"G = atom_length(abc, 7), \+ call(G).", 1, "   Fail: (1) atom_length(abc, 7)")]
    [InlineData(@"G = (atom_length(abc, N), atom_length(de, M)), call(G).", 1, "   Exit: (1) atom_length(de, 2)")]
    [InlineData(@"G = atom_length(abc), call(G, N).", 1, "   Exit: (1) atom_length(abc, 3)")]
    public void ABuiltinThatIsAGoalBuiltAtRunTime_IsTracedAsOneWrittenInAClause(
        string goal, int expected, string port)
    {
        // Written in the query, the same builtin goes through call_builtin,
        // which always reported it. The oracle is that trace.
        var lines = Trace("", goal, out int solutions);

        Assert.Equal(expected, solutions);
        Assert.Contains(lines, l => l.StartsWith("   Call: (1) atom_length(abc"));
        Assert.Contains(port, lines);
    }

    [Fact]
    public void BacktrackableBuiltin_ReportsARedoPerSolution()
    {
        var lines = Trace("", "between(1, 3, X).", out int solutions);

        Assert.Equal(3, solutions);
        Assert.Contains("   Exit: (1) between(1, 3, 1)", lines);
        Assert.Contains("   Exit: (1) between(1, 3, 2)", lines);
        Assert.Contains("   Exit: (1) between(1, 3, 3)", lines);
        Assert.Equal(2, lines.Count(l => l.StartsWith("   Redo: (1) between(")));
    }

    [Fact]
    public void EngineInternalGoals_AreNotTraced()
    {
        // The query wrapper and the $-prefixed control helpers are machinery,
        // not goals the user wrote.
        var lines = Trace("p :- ( true -> true ; true ).\n", "p.", out _);

        Assert.DoesNotContain(lines, l => l.Contains("__query__"));
        Assert.DoesNotContain(lines, l => l.Contains(" $"));
    }

    [Fact]
    public void HeapCollectionMidTrace_KeepsTheTracedArgumentsIntact()
    {
        // The tracer's copied argument cells are heap roots (ADR-016). p/1's
        // argument block is allocated at its call port and points into the heap
        // (its argument is a list). A collection then runs, more heap is
        // allocated on top, and only then is p redone — so its redo line can
        // only render [1] if the block was marked (not reclaimed) and relocated
        // (not left dangling).
        var lines = Trace(
            "p([1]).\np([2]).\n"
            + "build(0, []) :- !.\n"
            + "build(N, [N|T]) :- M is N - 1, build(M, T).\n"
            + "t :- p(X), garbage_collect, build(40, _), X = [2].\n",
            "t.", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Contains("   Exit: (2) p([1])", lines);
        Assert.Contains("   Redo: (2) p([1])", lines);
        Assert.Contains("   Exit: (2) p([2])", lines);
    }

    [Fact]
    public void NoSession_MeansNoPorts()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(1).");
        var sink = new StringWriter();
        engine.SetTracing(true, sink);
        engine.SetTracing(false);

        Assert.False(engine.Tracing);
        Assert.Single(engine.QueryAll("p(X)."));
        Assert.Equal("", sink.ToString());
    }

    [Fact]
    public void ABuiltinsNextSolution_HasItsExitPort_AndWhatFollowsIsNotUnderIt()
    {
        // The second solution of between/3 comes from a retry, which no
        // call_builtin instruction reports.
        var lines = Trace("ok(2).\n", "between(1, 2, X), ok(X).", out int solutions);

        Assert.Equal(1, solutions);
        int redo = lines.FindIndex(l => l.StartsWith("   Redo: (1) between("));
        Assert.True(redo >= 0, "no redo of between/3");
        Assert.Equal("   Exit: (1) between(1, 2, 2)", lines[redo + 1]);
        Assert.Equal("   Call: (1) ok(2)", lines[redo + 2]);
        Assert.Equal("   Exit: (1) ok(2)", lines[redo + 3]);
    }

    [Fact]
    public void AGoalThatRaisesUnderACatch_GetsAnExceptionPort_AndTheTraceGoesOn()
    {
        var lines = Trace(
            "p(X) :- atom_length(X, _).\nq(1).\n",
            "catch(p(f(x)), _, true), q(Y).", out int solutions);

        Assert.Equal(1, solutions);
        int raised = lines.FindIndex(l => l.StartsWith("   Call: (2) atom_length(f(x),"));
        Assert.True(raised >= 0, "atom_length/2 was not called");
        // The goals the ball left, innermost first, and then the rest of the
        // query at the depth it is at.
        Assert.Equal(new[]
        {
            "   Exception: (2) atom_length/2",
            "   Exception: (1) p/1",
            "   Exit: (1) q(1)",
        }, lines.Skip(raised + 1).Where(l => !l.StartsWith("   Call: (1) q(")).ToArray());
        Assert.DoesNotContain(lines, l => l.Contains("Exit: (2) atom_length") || l.Contains("Fail:"));
    }

    [Fact]
    public void AnErrorNobodyCatches_LeavesNothingOfItsQueryInTheNextTrace()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(X) :- atom_length(X, _).\nq(1).\n");
        var sink = new StringWriter();
        engine.SetTracing(true, sink);
        Assert.ThrowsAny<Exception>(() => engine.QueryAll("p(f(x)).").Count());
        sink.GetStringBuilder().Clear();

        Assert.Single(engine.QueryAll("q(Y)."));
        engine.SetTracing(false);

        var lines = sink.ToString().Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0).ToList();
        foreach (string l in lines) _log.WriteLine(l);
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("   Call: (1) q(", lines[0]);
        Assert.Equal("   Exit: (1) q(1)", lines[1]);
    }

    private const string Chain = ":- public r/2.\n"
        + "r(N, M) :- G = atom_length(abc, N), call(G), s(N, M).\n"
        + "s(N, M) :- M is N + 1, t(M).\n"
        + "t(_).\n";

    /// <summary>The ports of <paramref name="goal"/> on <paramref name="engine"/>,
    /// with no number of an unnamed variable.</summary>
    private List<string> TraceOn(PrologEngine engine, string goal)
    {
        var sink = new StringWriter();
        engine.SetTracing(true, sink);
        Assert.Single(engine.QueryAll(goal));
        engine.SetTracing(false);
        var lines = sink.ToString().Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"_G?\d+", "_"))
            .ToList();
        foreach (string l in lines) _log.WriteLine(l);
        return lines;
    }

    private static PrologEngine WithTheChainCompiled()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(Chain);
        Assert.True(engine.Query("r(_, _).").Success);
        Assert.True(engine.Query("r(_, _).").Success);
        Assert.True(engine.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.True(engine.IlPromotion.IsPromoted(Fid("r", 2)), "r/2 did not promote: the test would show nothing");
        return engine;
    }

    private static int Fid(string name, int arity)
        => Shumway.Core.FunctorTable.Intern(Shumway.Core.AtomTable.Intern(name).Id, arity);

    [Fact]
    public void CompiledPredicates_RunTheirBytecodeWhileTheTracerIsOn()
    {
        // The interpreter is what raises the ports: the oracle is an engine
        // that never compiled the program.
        var interpreted = new PrologEngine();
        interpreted.IlPromotion.Threshold = 0;
        interpreted.ConsultString(Chain);
        List<string> expected = TraceOn(interpreted, "r(N, M).");
        Assert.Contains("   Exit: (1) t(4)", expected);

        PrologEngine engine = WithTheChainCompiled();
        Assert.Equal(expected, TraceOn(engine, "r(N, M)."));

        // Nothing was dropped, and the compiled code serves again.
        Assert.False(engine.IlPromotion.CallsStayInterpreted);
        Assert.True(engine.IlPromotion.IsPromoted(Fid("r", 2)));
        Assert.True(engine.Query("r(N, M), N == 3, M == 4.").Success);
    }

    [Fact]
    public void TraceCalledFromAQueryThatWasSetUpCompiled_TracesWhatItCallsNext()
    {
        PrologEngine engine = WithTheChainCompiled();
        var sink = new StringWriter();
        engine.Out = sink;

        // trace/0 runs in a query whose calls were already linked to the
        // compiled code.
        Assert.True(engine.Query("trace, r(N, M), notrace, N == 3, M == 4.").Success);

        string[] lines = sink.ToString().Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0).ToArray();
        foreach (string l in lines) _log.WriteLine(l);
        Assert.Contains(lines, l => l.StartsWith("   Call: (2) atom_length(abc,"));
        Assert.Contains("   Exit: (2) atom_length(abc, 3)", lines);
        Assert.Contains("   Call: (1) t(4)", lines);
        Assert.Contains("   Exit: (1) t(4)", lines);
        Assert.False(engine.IlPromotion.CallsStayInterpreted);
    }

    [Fact]
    public void TracePredicate_TurnsTracingOnMidQuery()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(1).
            q :- trace, p(_), notrace.
            """);
        var sink = new StringWriter();
        engine.Out = sink;

        Assert.Single(engine.QueryAll("q."));

        string output = sink.ToString();
        Assert.Contains("Call: (1) p(", output);
        Assert.Contains("Exit: (1) p(1)", output);
        Assert.False(engine.Tracing);   // notrace/0 detached it again
    }
}
