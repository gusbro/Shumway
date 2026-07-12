using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 phase D1 — breakpoints. Debug-compiled code emits NO extra instructions;
/// it records, per predicate, the offsets a debugger may stop at (clause entries and
/// the first instruction of each body goal) against interned source sites. Arming a
/// breakpoint patches the single opcode byte at such an offset to <c>Break</c> and
/// remembers what was there; hitting it reports the stop and then runs the original
/// instruction from the table, at the same pc, with its operands untouched. The byte
/// is never restored — which is what lets a second activation over the same shared
/// code space hit the same breakpoint instead of racing through a restore window.
///
/// <para>The consequence worth pinning: debug-compiled code with no breakpoints
/// armed runs exactly the instructions release code would.</para>
/// </summary>
public class Adr035BreakTests
{
    private readonly ITestOutputHelper _log;

    public Adr035BreakTests(ITestOutputHelper log) => _log = log;

    /// <summary>Records every breakpoint hit, as the source line it stopped on.</summary>
    private sealed class Recorder : IDebugSession
    {
        private readonly PrologEngine _engine;
        public readonly List<int> Lines = new();

        public Recorder(PrologEngine engine) => _engine = engine;

        public void OnBreak(Activation engine, int pc)
        {
            int siteId = _engine.SiteAt(pc);
            Lines.Add(DebugSiteTable.Get(siteId).Line);
        }

        public void OnCallAddress(Activation e, int a, bool t) { }
        public void OnCallFunctor(Activation e, int f, bool t) { }
        public void OnCallBuiltin(Activation e, int b, bool t) { }
        public void OnBuiltinResult(Activation e, int b, bool ok) { }
        public void OnExit(Activation e) { }
        public void OnRedo(Activation e, int pc) { }
        public void OnFail(Activation e) { }
        public void MarkHeapRoots(Action<int> mark) { }
        public void RelocateHeapRoots(Func<int, int> reloc) { }
    }

    /// <summary>Consults <paramref name="program"/> in debug mode. Line 1 is the
    /// compile_mode directive, so the program's own first line is 2.</summary>
    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        return engine;
    }

    private List<int> HitsFor(PrologEngine engine, string goal, out int solutions)
    {
        var rec = new Recorder(engine);
        engine.AttachDebugSession(rec);
        solutions = engine.QueryAll(goal).Count();
        engine.AttachDebugSession(null);
        _log.WriteLine("stopped on lines: " + string.Join(", ", rec.Lines));
        return rec.Lines;
    }

    [Fact]
    public void ABreakpointOnAFactsLine_StopsWhenTheClauseIsTried()
    {
        var engine = DebugEngine("p(a).\np(b).\n");   // lines 2, 3

        Assert.Equal(1, engine.AddBreakpoint("<string>", 3));
        var hits = HitsFor(engine, "p(X).", out int solutions);

        Assert.Equal(2, solutions);
        Assert.Equal(new[] { 3 }, hits);   // only p(b)'s clause, and only once
    }

    [Fact]
    public void ABreakpointOnABodyGoal_StopsBeforeTheGoalRuns()
    {
        //   2: top(X) :- mid(X), tail(X).
        //   3: mid(7).
        //   4: tail(7).
        var engine = DebugEngine("top(X) :- mid(X), tail(X).\nmid(7).\ntail(7).\n");

        Assert.True(engine.AddBreakpoint("<string>", 4) > 0);   // tail/1's clause entry
        var hits = HitsFor(engine, "top(A).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Equal(new[] { 4 }, hits);
    }

    [Fact]
    public void NoBreakpoints_MeansNoStops_AndNoCost()
    {
        // The whole point of the patch model: debug-compiled code that nobody is
        // stopping in runs the same instructions release code would.
        var engine = DebugEngine("top(X) :- mid(X).\nmid(7).\n");

        var hits = HitsFor(engine, "top(A).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Empty(hits);
    }

    [Fact]
    public void ABreakpointStopsOnEveryPassThroughIt()
    {
        //   2: loop(0) :- !.
        //   3: loop(N) :- M is N - 1, loop(M).
        var engine = DebugEngine("loop(0) :- !.\nloop(N) :- M is N - 1, loop(M).\n");

        Assert.True(engine.AddBreakpoint("<string>", 3) > 0);
        var hits = HitsFor(engine, "loop(5).", out int solutions);

        Assert.Equal(1, solutions);
        // Clause 2 is entered for N = 5..1, and its two body goals sit on line 3
        // as well — so every pass reports, and the run still completes.
        Assert.All(hits, l => Assert.Equal(3, l));
        Assert.True(hits.Count >= 5, $"expected at least 5 stops, got {hits.Count}");
    }

    [Fact]
    public void RemovingABreakpoint_RestoresTheOriginalInstruction()
    {
        var engine = DebugEngine("p(a).\np(b).\n");

        engine.AddBreakpoint("<string>", 3);
        Assert.NotEmpty(HitsFor(engine, "p(X).", out _));

        engine.RemoveBreakpoint("<string>", 3);
        Assert.Empty(engine.Breakpoints);
        Assert.Empty(HitsFor(engine, "p(X).", out int solutions));
        Assert.Equal(2, solutions);   // and the program still runs correctly
    }

    [Fact]
    public void ABreakpointSurvivesAcrossQueries()
    {
        // The armed SITE is the truth; the byte patches are re-derived per query,
        // because the code space can be relinked or compacted between them.
        var engine = DebugEngine("p(a).\np(b).\n");
        engine.AddBreakpoint("<string>", 2);

        Assert.Single(HitsFor(engine, "p(a).", out _));
        Assert.Single(HitsFor(engine, "p(a).", out _));
        Assert.Single(HitsFor(engine, "p(a).", out _));
    }

    [Fact]
    public void ABreakpointOnALineWithNoCode_DoesNotBind()
    {
        // Zero sites is how a debugger learns to draw a hollow breakpoint instead
        // of pretending it took.
        var engine = DebugEngine("p(a).\n\n% just a comment\n");

        Assert.Equal(0, engine.AddBreakpoint("<string>", 3));
        Assert.Equal(0, engine.AddBreakpoint("<string>", 4));
        Assert.Empty(engine.Breakpoints);
    }

    [Fact]
    public void ReleaseCode_HasNoStopSites_SoNothingBinds()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(a).\np(b).\n");

        Assert.Equal(0, engine.AddBreakpoint("<string>", 1));
        Assert.Empty(HitsFor(engine, "p(X).", out int solutions));
        Assert.Equal(2, solutions);
    }

    [Fact]
    public void BreakpointsDoNotChangeWhatTheProgramComputes()
    {
        var engine = DebugEngine(
            "app([], L, L).\napp([H|T], L, [H|R]) :- app(T, L, R).\n"
            + "rev([], []).\nrev([H|T], R) :- rev(T, RT), app(RT, [H], R).\n");

        // Break inside the hot recursion, in both predicates.
        engine.AddBreakpoint("<string>", 3);
        engine.AddBreakpoint("<string>", 5);

        var rec = new Recorder(engine);
        engine.AttachDebugSession(rec);
        var result = engine.QueryFirst<List<int>>("rev([1,2,3], R).", "R");
        engine.AttachDebugSession(null);

        Assert.Equal(new[] { 3, 2, 1 }, result);
        Assert.NotEmpty(rec.Lines);   // and it really did stop along the way
    }

    [Fact]
    public void SitesResolveBackToTheirSourceLocation()
    {
        int file = DebugSiteTable.InternFile("<string>");
        var engine = DebugEngine("distinctly_named(42).\n");   // line 2
        engine.QueryAll("distinctly_named(_).").ToList();

        var sites = DebugSiteTable.SitesOnLine(file, 2);
        Assert.NotEmpty(sites);
        var site = DebugSiteTable.Get(sites[0]);
        Assert.Equal(2, site.Line);
        Assert.Equal("<string>", DebugSiteTable.FileName(site.FileId));
    }
}
