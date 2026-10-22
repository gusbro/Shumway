using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 — a predicate a running query consults is linked into its code as it
/// runs. It is debuggable there like any other: a stop under it shows its frame, and a
/// breakpoint in it stops the query that loaded it.</summary>
[Collection("debugger")]
public sealed class Adr035LiveLinkTests : IDisposable
{
    private readonly ITestOutputHelper _log;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shumway-live-debug-{Guid.NewGuid():N}");

    public Adr035LiveLinkTests(ITestOutputHelper log)
    {
        _log = log;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Recorder : IDebugSession
    {
        // net48 has no default interface implementations.
        void IDebugSession.OnInlineGoal(Activation engine) { }
        void IDebugSession.OnLeaveProlog(Activation engine) { }

        private readonly PrologEngine _engine;
        public readonly List<string> Stops = new();

        public Recorder(PrologEngine engine) => _engine = engine;

        public void OnBreak(Activation engine, int pc)
            => Stops.Add(string.Join(" < ",
                _engine.CaptureFrames(engine).Take(2).Select(f => $"{f.Name}/{f.Arity}:{f.Line}")));

        public void OnCallAddress(Activation e, int a, bool t) { }
        public void OnCallFunctor(Activation e, int f, bool t) { }
        public void OnCallBuiltin(Activation e, int b, bool t) { }
        public void OnBuiltinResult(Activation e, int b, bool ok) { }
        public void OnExit(Activation e) { }
        public void OnRedo(Activation e, int pc) { }
        public void OnFail(Activation e) { }
        public void MarkHeapRoots(Action<int> mark) { }
        public void RelocateHeapRoots(Activation engine, Func<int, int> reloc, Func<int, int> relocBoundary) { }
    }

    /// <summary>What the tests run from inside a query. Registered once: a foreign
    /// predicate's name is taken process-wide; the tests of a collection run one at a
    /// time.</summary>
    public static class Host
    {
        public static Action<Activation>? Run;
        private static bool _registered;

        public static void Register(PrologEngine engine, Action<Activation> run)
        {
            Run = run;
            lock (typeof(Host))
            {
                if (_registered) return;
                engine.RegisterPredicates(typeof(Host));
                _registered = true;
            }
        }

        [PrologPredicate("live_host_run/0")]
        public static bool RunIt(Activation engine)
        {
            Run!(engine);
            return true;
        }
    }

    /// <summary>
    /// <code>
    ///  1: mid(X) :-
    ///  2:     after(X),
    ///  3:     atom(X).
    /// </code></summary>
    private string LiveFile(int extraPredicates = 0)
    {
        var text = new StringBuilder("mid(X) :-\n    after(X),\n    atom(X).\n");
        for (int k = 0; k < extraPredicates; k++) text.Append($"e{k}(X) :- atom(X), e{k}x(X).\ne{k}x(_).\n");
        string path = Path.Combine(_dir, "live.pl").Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(path, text.ToString());
        return path;
    }

    /// <summary><c>2: after(X) :- X = done.</c></summary>
    private static PrologEngine Engine()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\nafter(X) :- X = done.\n");
        return engine;
    }

    [Fact]
    public void AStopUnderAPredicateTheQueryConsulted_ShowsItsFrame()
    {
        string live = LiveFile();
        var engine = Engine();
        Assert.True(engine.AddBreakpoint("<string>", 2) > 0);

        var rec = new Recorder(engine);
        engine.AttachDebugSession(rec);
        var sols = engine.QueryAll($"consult('{live}'), mid(X).").ToList();
        engine.AttachDebugSession(null);
        _log.WriteLine(string.Join(" | ", rec.Stops));

        Assert.Single(sols);
        Assert.Equal(new[] { "after/1:2 < mid/1:2" }, rec.Stops);
    }

    /// <summary>A clause the query asserted has no predicate linked for it and no source:
    /// its frame is a dynamic clause, not the predicate linked before it.</summary>
    [Fact]
    public void AStopUnderAClauseTheQueryAsserted_ShowsADynamicClauseFrame()
    {
        var engine = Engine();
        Assert.True(engine.AddBreakpoint("<string>", 2) > 0);

        var rec = new Recorder(engine);
        engine.AttachDebugSession(rec);
        var sols = engine.QueryAll("assertz((am(X) :- after(X), atom(X))), am(X).").ToList();
        engine.AttachDebugSession(null);
        _log.WriteLine(string.Join(" | ", rec.Stops));

        Assert.Single(sols);
        Assert.Equal(new[] { "after/1:2 < <dynamic clause>/-1:0" }, rec.Stops);
    }

    /// <summary>The breakpoint is set before its file is loaded; the query loads the file
    /// and calls into it.</summary>
    [Fact]
    public void ABreakpointInAFileTheQueryConsults_StopsInThatQuery()
    {
        string live = LiveFile();
        var engine = Engine();
        Assert.Equal(0, engine.AddBreakpoint(live, 2));   // nothing loaded there yet

        var rec = new Recorder(engine);
        engine.AttachDebugSession(rec);
        var sols = engine.QueryAll($"consult('{live}'), mid(X).").ToList();
        engine.AttachDebugSession(null);
        _log.WriteLine(string.Join(" | ", rec.Stops));

        Assert.Single(sols);
        Assert.NotEmpty(rec.Stops);
        Assert.Equal("mid/1:2", rec.Stops[0]);
    }

    [Fact]
    public void ABreakpointSetWhileTheQueryRuns_StopsInCodeTheQueryConsulted()
    {
        string live = LiveFile();
        var engine = Engine();
        int bound = -1;
        Host.Register(engine, _ => bound = engine.AddBreakpoint(live, 2));

        var rec = new Recorder(engine);
        engine.AttachDebugSession(rec);
        var sols = engine.QueryAll($"consult('{live}'), live_host_run, mid(X).").ToList();
        engine.AttachDebugSession(null);
        _log.WriteLine($"bound {bound}: " + string.Join(" | ", rec.Stops));

        Assert.True(bound > 0);
        Assert.Single(sols);
        Assert.NotEmpty(rec.Stops);
        Assert.Equal("mid/1:2", rec.Stops[0]);
    }

    /// <summary>A query run from inside another shares its code array until it outgrows
    /// it: the code it consults no longer fits, and it moves to a larger copy. Each array
    /// keeps its own breakpoints from then on: the nested query stops in the code it
    /// consulted, and the outer one in its own.</summary>
    [Fact]
    public void AQueryThatOutgrowsTheArrayItShares_KeepsTheBreakpointsOfBoth()
    {
        var engine = Engine();
        Assert.True(engine.AddBreakpoint("<string>", 2) > 0);
        string live = LiveFile(extraPredicates: 4000);
        Assert.Equal(0, engine.AddBreakpoint(live, 2));

        byte[]? outerArray = null, nestedBefore = null, nestedAfter = null;
        int nestedSolutions = 0;
        Host.Register(engine, outer =>
        {
            outerArray = outer.CurrentProgram;
            int calls = 0;
            Host.Run = nested =>
            {
                if (calls++ == 0) nestedBefore = nested.CurrentProgram;
                else nestedAfter = nested.CurrentProgram;
            };
            nestedSolutions = engine.QueryAll(
                $"live_host_run, consult('{live}'), live_host_run, mid(Y), atom(Y).").Count();
        });
        var rec = new Recorder(engine);
        engine.AttachDebugSession(rec);
        var sols = engine.QueryAll("live_host_run, after(Z).").ToList();
        engine.AttachDebugSession(null);
        _log.WriteLine(string.Join(" | ", rec.Stops));

        Assert.Single(sols);
        Assert.Equal(1, nestedSolutions);
        Assert.Same(outerArray, nestedBefore);       // the nested query started on it
        Assert.NotSame(nestedBefore, nestedAfter);   // and moved off it
        Assert.Contains(rec.Stops, x => x.StartsWith("mid/1:2"));   // the nested query's own code
        Assert.Equal("after/1:2", rec.Stops[^1]);   // the outer query's, after it
    }

    /// <summary>A breakpoint removed while the nested query runs on the array it moved to
    /// comes out of that array; the outer query's array still holds its Break byte, and
    /// takes it out when the outer query is current again, from its own table.</summary>
    [Fact]
    public void ABreakpointRemovedWhileAMovedQueryRuns_IsGoneForTheQueryItIsNestedIn()
    {
        var engine = Engine();
        Assert.True(engine.AddBreakpoint("<string>", 2) > 0);
        string live = LiveFile(extraPredicates: 4000);

        byte[]? outerArray = null, nestedBefore = null, nestedAfter = null;
        Host.Register(engine, outer =>
        {
            outerArray = outer.CurrentProgram;
            int calls = 0;
            Host.Run = nested =>
            {
                if (calls++ == 0) { nestedBefore = nested.CurrentProgram; return; }
                nestedAfter = nested.CurrentProgram;
                engine.RemoveBreakpoint("<string>", 2);
            };
            engine.QueryAll($"live_host_run, consult('{live}'), live_host_run.").ToList();
        });
        var rec = new Recorder(engine);
        engine.AttachDebugSession(rec);
        var sols = engine.QueryAll("live_host_run, after(Z).").ToList();
        engine.AttachDebugSession(null);
        _log.WriteLine(string.Join(" | ", rec.Stops));

        Assert.Same(outerArray, nestedBefore);
        Assert.NotSame(nestedBefore, nestedAfter);
        Assert.Single(sols);
        Assert.Empty(rec.Stops);
    }
}
