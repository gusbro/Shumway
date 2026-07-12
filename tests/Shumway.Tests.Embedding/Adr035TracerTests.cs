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
        // The whole point of copying argument CELLS at the call port: the exit
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
        // Note is/2 would NOT show up: ADR-018 compiles arithmetic inline, so
        // there is no builtin dispatch to report. atom_length/2 is a real one.
        var lines = Trace("", "atom_length(abc, N).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Contains(lines, l => l.StartsWith("   Call: (1) atom_length(abc,"));
        Assert.Contains("   Exit: (1) atom_length(abc, 3)", lines);
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
        // allocated on top, and only THEN is p redone — so its redo line can
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
        engine.ConsultString("p(1).\n");
        var sink = new StringWriter();
        engine.SetTracing(true, sink);
        engine.SetTracing(false);

        Assert.False(engine.Tracing);
        Assert.Single(engine.QueryAll("p(X)."));
        Assert.Equal("", sink.ToString());
    }

    [Fact]
    public void TracePredicate_TurnsTracingOnMidQuery()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(1).\nq :- trace, p(_), notrace.\n");
        var sink = new StringWriter();
        engine.Out = sink;

        Assert.Single(engine.QueryAll("q."));

        string output = sink.ToString();
        Assert.Contains("Call: (1) p(", output);
        Assert.Contains("Exit: (1) p(1)", output);
        Assert.False(engine.Tracing);   // notrace/0 detached it again
    }
}
