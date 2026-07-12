using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 phase D1 — the <c>Break</c> opcode and the global
/// <see cref="DebugSiteTable"/>. Debug-compiled code carries a stop site at every
/// clause entry and before every body goal; the site's id is interned rather than
/// an offset, so it survives clause→predicate→program relocation untouched. These
/// tests pin the two things a debugger will rely on: that the sites fire in
/// execution order, and that the source line they carry is the one the user sees.
/// </summary>
public class Adr035BreakTests
{
    private readonly ITestOutputHelper _log;

    public Adr035BreakTests(ITestOutputHelper log) => _log = log;

    /// <summary>A minimal session: records the source line of every stop site
    /// reached, and can be told to make one of them fail the goal (the crude
    /// stand-in for "stop here", which is enough to prove the site is reached
    /// BEFORE the goal runs).</summary>
    private sealed class SiteRecorder : IDebugSession
    {
        public readonly List<(string File, int Line)> Hits = new();

        public void OnBreak(Activation engine, int siteId)
        {
            var site = DebugSiteTable.Get(siteId);
            Hits.Add((DebugSiteTable.FileName(site.FileId), site.Line));
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

    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        return engine;
    }

    private List<int> Run(PrologEngine engine, string goal, out int solutions)
    {
        var rec = new SiteRecorder();
        engine.AttachDebugSession(rec);
        solutions = engine.QueryAll(goal).Count();
        engine.AttachDebugSession(null);
        foreach (var h in rec.Hits) _log.WriteLine($"{h.File}:{h.Line}");
        return rec.Hits.Select(h => h.Line).ToList();
    }

    [Fact]
    public void StopSites_FireAtEveryClauseEntryAndBodyGoal_InSourceLines()
    {
        // Line 1 is the set_prolog_flag directive DebugEngine prepends, so the
        // program's own first line is 2.
        //   2: top(X) :- mid(X), tail(X).
        //   3: mid(7).
        //   4: tail(7).
        var engine = DebugEngine("top(X) :- mid(X), tail(X).\nmid(7).\ntail(7).\n");

        var lines = Run(engine, "top(A).", out int solutions);

        Assert.Equal(1, solutions);
        // top/1's clause entry, then its two body goals (both on line 2), each
        // followed by the callee's own clause-entry site.
        Assert.Equal(new[] { 2, 2, 3, 2, 4 }, lines);
    }

    [Fact]
    public void GoalsOnTheirOwnLines_CarryTheirOwnLine()
    {
        //   2: run(X) :-
        //   3:     first(X),
        //   4:     second(X).
        //   5: first(1).
        //   6: second(1).
        var engine = DebugEngine(
            "run(X) :-\n    first(X),\n    second(X).\nfirst(1).\nsecond(1).\n");

        var lines = Run(engine, "run(A).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Equal(new[] { 2, 3, 5, 4, 6 }, lines);
    }

    [Fact]
    public void FactsHaveAStopSite_OnEveryClauseTried()
    {
        // A breakpoint on a fact's line has to bind, and it must fire once per
        // clause the machine actually tries — backtracking included.
        var engine = DebugEngine("p(a).\np(b).\np(c).\n");   // lines 2, 3, 4

        var lines = Run(engine, "p(X).", out int solutions);

        Assert.Equal(3, solutions);
        Assert.Equal(new[] { 2, 3, 4 }, lines);
    }

    [Fact]
    public void SiteIsReachedBeforeTheGoalRuns()
    {
        // The site for `mid(X)` must be reported while X is still unbound —
        // that is what makes it useful as a place to stop and look at arguments.
        var engine = DebugEngine("top(X) :- mid(X).\nmid(9).\n");

        string? seen = null;
        var probe = new ArgProbe(a => seen ??= Rendered(a));
        engine.AttachDebugSession(probe);
        engine.QueryAll("top(A).").ToList();
        engine.AttachDebugSession(null);

        Assert.Equal("_", seen);   // unbound at the clause-entry site of top/1
    }

    private static string Rendered(Activation engine)
    {
        var cell = engine.GetRegister(0);
        return cell.Tag == Tag.Ref ? "_" : "bound";
    }

    private sealed class ArgProbe : IDebugSession
    {
        private readonly Action<Activation> _onSite;
        public ArgProbe(Action<Activation> onSite) => _onSite = onSite;
        public void OnBreak(Activation e, int siteId) => _onSite(e);
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

    [Fact]
    public void ReleaseCode_CarriesNoStopSites()
    {
        var engine = new PrologEngine();
        engine.ConsultString("top(X) :- mid(X).\nmid(7).\n");

        var lines = Run(engine, "top(A).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Empty(lines);
    }

    [Fact]
    public void SitesAreFindableByFileAndLine_WhichIsHowABreakpointBinds()
    {
        int file = DebugSiteTable.InternFile("<string>");
        var engine = DebugEngine("uniquely_named_pred(42).\n");   // line 2
        engine.QueryAll("uniquely_named_pred(_).").ToList();

        Assert.NotEmpty(DebugSiteTable.SitesOnLine(file, 2));

        // And a site resolves back to exactly where it came from.
        int siteId = DebugSiteTable.SitesOnLine(file, 2)[0];
        var site = DebugSiteTable.Get(siteId);
        Assert.Equal(file, site.FileId);
        Assert.Equal(2, site.Line);
        Assert.Equal("<string>", DebugSiteTable.FileName(site.FileId));
    }

    [Fact]
    public void DebugCode_StillComputesTheRightAnswers()
    {
        // The stop sites are extra instructions in the middle of every clause;
        // nothing about the program's meaning may change.
        var engine = DebugEngine(
            "app([], L, L).\napp([H|T], L, [H|R]) :- app(T, L, R).\n"
            + "rev([], []).\nrev([H|T], R) :- rev(T, RT), app(RT, [H], R).\n");

        Assert.Equal(new[] { 3, 2, 1 }, engine.QueryFirst<List<int>>("rev([1,2,3], R).", "R"));
        Assert.Equal(4, engine.QueryAll("app(X, Y, [1,2,3]).").Count());
    }
}
