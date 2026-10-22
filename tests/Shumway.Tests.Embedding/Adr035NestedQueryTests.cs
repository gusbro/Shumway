using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Shumway.Core;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 — a query nested in a running one (a directive of a file the running
/// query consults) links a program of its own: its address map, its debugger tables and,
/// when the consult rebuilds the persistent buffer, a buffer and breakpoint record of its
/// own. The running query keeps its own when it resumes: a stop in its code reports its
/// line and frames, the Break bytes in its buffer run the instructions they replaced, and
/// an arm that landed while the nested query ran reaches it.</summary>
[Collection("debugger")]
public sealed class Adr035NestedQueryTests : IDisposable
{
    private readonly ITestOutputHelper _log;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shumway-nested-debug-{Guid.NewGuid():N}");

    public Adr035NestedQueryTests(ITestOutputHelper log)
    {
        _log = log;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>What the consulted file's directive saw, inside the nested query.</summary>
    public sealed class NestedProbe
    {
        public byte[]? NestedBuffer;
        public Action? OnNested;
    }

    /// <summary>Registered once: a foreign predicate's name is taken process-wide. The
    /// tests of a collection run one at a time.</summary>
    public static class NestedProbeHost
    {
        public static NestedProbe? Current;
        private static bool _registered;

        public static void Register(PrologEngine engine, NestedProbe probe)
        {
            Current = probe;
            lock (typeof(NestedProbeHost))
            {
                if (_registered) return;
                engine.RegisterPredicates(typeof(NestedProbeHost));
                _registered = true;
            }
        }

        [PrologPredicate("nested_probe/0")]
        public static bool Probe(Activation engine)
        {
            var probe = Current!;
            probe.NestedBuffer = engine.CurrentProgram;
            probe.OnNested?.Invoke();
            return true;
        }
    }

    private sealed class Recorder : IDebugSession
    {
        // net48 has no default interface implementations.
        void IDebugSession.OnInlineGoal(Activation engine) { }
        void IDebugSession.OnLeaveProlog(Activation engine) { }

        private readonly PrologEngine _engine;
        public readonly List<string> Stops = new();
        public byte[]? StoppedIn;

        public Recorder(PrologEngine engine) => _engine = engine;

        public void OnBreak(Activation engine, int pc)
        {
            int site = _engine.SiteAt(pc);
            var frames = _engine.CaptureFrames(engine);
            Stops.Add((site < 0 ? "no site" : $"line {DebugSiteTable.Get(site).Line}") + ": "
                + string.Join(" < ", frames.Take(2).Select(f => $"{f.Name}/{f.Arity}:{f.Line}")));
            StoppedIn = engine.CurrentProgram;
        }

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

    /// <summary>A debug-compiled engine whose go/1 consults a file of <paramref name="m"/>
    /// new predicates and a directive, then calls after/1 (not last, so go/1 keeps its
    /// frame):
    /// <code>
    ///  2: go(X) :- consult(Extra), after(X), atom(X).
    ///  3: after(X) :- X = done.
    /// </code></summary>
    private PrologEngine Engine(int m, NestedProbe probe)
    {
        string extra = Path.Combine(_dir, "extra.pl").Replace(Path.DirectorySeparatorChar, '/');
        var lines = new StringBuilder();
        for (int k = 0; k < m; k++) lines.Append($"e{k}(X) :- atom(X), e{k}x(X).\ne{k}x(_).\n");
        lines.Append(":- nested_probe.\n");
        File.WriteAllText(extra, lines.ToString());
        var engine = new PrologEngine();
        NestedProbeHost.Register(engine, probe);
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n"
            + $"go(X) :- consult('{extra}'), after(X), atom(X).\n"
            + "after(X) :- X = done.\n");
        return engine;
    }

    [Fact]
    public void AStopAfterANestedQuery_ReportsTheRunningQuerysLineAndFrames()
    {
        int ownBuffer = 0;
        for (int m = 0; m <= 12; m++)
        {
            var probe = new NestedProbe();
            var engine = Engine(m, probe);
            Assert.True(engine.AddBreakpoint("<string>", 3) > 0);
            var rec = new Recorder(engine);
            engine.AttachDebugSession(rec);
            var sols = engine.QueryAll("go(X).").ToList();
            engine.AttachDebugSession(null);
            _log.WriteLine($"m = {m}: {string.Join(" | ", rec.Stops)}");

            Assert.Single(sols);
            Assert.Equal("done", sols[0]["X"]!.ToString());
            Assert.Equal(new[] { "line 3: after/1:3 < go/1:2" }, rec.Stops);
            if (!ReferenceEquals(probe.NestedBuffer, rec.StoppedIn)) ownBuffer++;
        }
        // The case the per-buffer breakpoint record is for: the nested query ran on a
        // buffer the running one does not, and armed the breakpoint in it too.
        Assert.True(ownBuffer > 0, "no nested query ran on a buffer of its own");
    }

    [Fact]
    public void AnArmWhileANestedQueryRuns_ReachesTheQueryItIsNestedIn()
    {
        for (int m = 0; m <= 6; m++)
        {
            var probe = new NestedProbe();
            var engine = Engine(m, probe);
            int stops = 0;
            ChannelDebugSession? session = null;
            session = new ChannelDebugSession(engine, _ =>
            {
                stops++;
                session!.Channel.WriteCommands(new DebugCommand(DebugCommandKind.Continue));
            }) { ActivateOnAttach = true };
            engine.DebugFullyArmed = false;
            engine.DebugLcoWhenArmed = false;
            engine.SetDebugLastCall(true);
            Assert.True(engine.AddBreakpoint("<string>", 3) > 0);
            // The arm lands on the activation running then: the nested one.
            probe.OnNested = () => session!.ActivateFullDebug();

            List<Solution> sols;
            using (session) sols = engine.QueryAll("go(X).").ToList();

            Assert.Single(sols);
            Assert.Equal("done", sols[0]["X"]!.ToString());
            Assert.True(stops == 1, $"m = {m}: {stops} stops at after/1");
        }
    }

    /// <summary>A breakpoint set before its file is loaded binds when a query consults the
    /// file: the rebind links the new code in a query nested in that one.</summary>
    [Fact]
    public void APendingBreakpointBindsWhenAQueryConsultsItsFile()
    {
        //  1: mid(X) :-
        //  2:     after(X),
        //  3:     atom(X).
        string live = Path.Combine(_dir, "live.pl").Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(live, "mid(X) :-\n    after(X),\n    atom(X).\n");
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\nafter(X) :- X = done.\n");
        Assert.Equal(0, engine.AddBreakpoint(live, 2));   // nothing loaded there yet

        var rec = new Recorder(engine);
        engine.AttachDebugSession(rec);
        engine.QueryAll($"consult('{live}').").ToList();
        var sols = engine.QueryAll("mid(X).").ToList();
        engine.AttachDebugSession(null);
        _log.WriteLine(string.Join(" | ", rec.Stops));

        Assert.Single(sols);
        Assert.NotEmpty(rec.Stops);
        Assert.StartsWith("line 2: mid/1:2", rec.Stops[0]);
    }
}
