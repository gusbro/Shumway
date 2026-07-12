using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 phase D1 — <c>debug_lastcall</c>: last-call optimisation as a runtime
/// switch. Code compiled under <c>compile_mode=debug</c> carries the opcode; the
/// <c>debug_lco</c> flag decides, per dispatch, whether it behaves as
/// <c>deallocate; execute</c> (LCO on — what release code does) or as a plain
/// <c>call</c> that keeps the caller's frame alive (LCO off — what a debugger
/// needs, since that frame is the goal's stack entry and holds its variables).
/// </summary>
public class Adr035LastCallTests
{
    private readonly ITestOutputHelper _log;

    public Adr035LastCallTests(ITestOutputHelper log) => _log = log;

    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        return engine;
    }

    private List<string> Trace(PrologEngine engine, string goal, out int solutions)
    {
        var sink = new StringWriter();
        engine.SetTracing(true, sink);
        solutions = engine.QueryAll(goal).Count();
        engine.SetTracing(false);

        var lines = sink.ToString().Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .ToList();
        foreach (string l in lines) _log.WriteLine(l);
        return lines;
    }

    [Fact]
    public void DebugCode_RunsIdentically_WithLcoOn()
    {
        // The default is LCO on: debug-compiled code must behave exactly like
        // release code until someone asks for the full stack.
        var engine = DebugEngine(
            "app([], L, L).\napp([H|T], L, [H|R]) :- app(T, L, R).\n");

        var sols = engine.Query<List<int>>("app([1,2,3], [4,5], X).", "X").ToList();

        Assert.Single(sols);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, sols[0]);
        Assert.Equal("on", engine.QueryFirst<string>("current_prolog_flag(debug_lco, F).", "F"));
    }

    [Fact]
    public void LcoOff_GivesEveryPredicateAnExitPort()
    {
        // With LCO on, top/1's only body goal is a tail call, so top's frame is
        // gone before mid/1 runs and top never reports an exit. Turning LCO off
        // is precisely what puts that exit port back.
        var engine = DebugEngine("top(X) :- mid(X).\nmid(X) :- leaf(X).\nleaf(7).\n");

        var withLco = Trace(engine, "top(A).", out _);
        Assert.DoesNotContain(withLco, l => l.StartsWith("   Exit: ") && l.Contains(" top("));

        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        var noLco = Trace(engine, "top(A).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Contains("   Exit: (3) leaf(7)", noLco);
        Assert.Contains("   Exit: (2) mid(7)", noLco);
        Assert.Contains("   Exit: (1) top(7)", noLco);
    }

    [Fact]
    public void LcoOff_NestsTheCallStack_InsteadOfFlatteningIt()
    {
        var engine = DebugEngine("top(X) :- mid(X).\nmid(X) :- leaf(X).\nleaf(7).\n");

        var withLco = Trace(engine, "top(A).", out _);
        // Flat: each tail call takes its caller's place.
        Assert.Contains("   Call: (1) mid(_G0)", withLco);
        Assert.Contains("   Call: (1) leaf(_G0)", withLco);

        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        var noLco = Trace(engine, "top(A).", out _);

        Assert.Contains("   Call: (1) top(_G0)", noLco);
        Assert.Contains("   Call: (2) mid(_G0)", noLco);
        Assert.Contains("   Call: (3) leaf(_G0)", noLco);
    }

    [Fact]
    public void LcoOff_PreservesSemantics_IncludingBacktrackingAndCut()
    {
        var engine = DebugEngine(
            "p(1).\np(2).\np(3).\n"
            + "q(X) :- p(X), X > 1, !.\n"
            + "r(L) :- findall(X, p(X), L).\n");
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

        Assert.Equal(2, engine.QueryFirst<int>("q(X).", "X"));
        Assert.Equal(3, engine.QueryAll("p(X).").Count());
        Assert.Equal(new[] { 1, 2, 3 }, engine.QueryFirst<List<int>>("r(L).", "L"));
    }

    [Fact]
    public void LcoOff_MakesDeepTailRecursionUseRealStack()
    {
        // The trade-off, stated: without LCO a tail-recursive loop keeps one
        // frame per iteration. Small depths must still be correct; that a deep
        // one now costs stack is the price of a full call stack, and exactly
        // why LCO is a switch rather than "always off under debug".
        var engine = DebugEngine("count(0, 0).\ncount(N, M) :- N > 0, P is N - 1, count(P, M).\n");
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

        Assert.Equal(0, engine.QueryFirst<int>("count(200, M).", "M"));

        // And back on: the same program runs in constant stack again.
        engine.QueryAll("set_prolog_flag(debug_lco, on).").ToList();
        Assert.Equal(0, engine.QueryFirst<int>("count(100000, M).", "M"));
    }

    [Fact]
    public void LcoToggle_TakesEffectOnTheRunningQuery()
    {
        // A debugger flips this from the Immediate window mid-session, so the
        // flip has to reach the activation that is already running.
        var engine = DebugEngine("top(X) :- mid(X).\nmid(X) :- leaf(X).\nleaf(7).\n");
        var sink = new StringWriter();
        engine.SetTracing(true, sink);

        engine.QueryAll("set_prolog_flag(debug_lco, off), top(A).").ToList();
        engine.SetTracing(false);

        string output = sink.ToString();
        _log.WriteLine(output);
        Assert.Contains("Exit: (1) top(7)", output);
    }

    [Fact]
    public void ReleaseCode_HasNoDebugLastCall_AndIgnoresTheFlag()
    {
        // Release-compiled code carries no debug_lastcall at all, so the flag is
        // inert for it — turning LCO off must not resurrect frames that were
        // never emitted.
        var engine = new PrologEngine();
        engine.ConsultString("top(X) :- mid(X).\nmid(X) :- leaf(X).\nleaf(7).\n");
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

        var lines = Trace(engine, "top(A).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.DoesNotContain(lines, l => l.StartsWith("   Exit: ") && l.Contains(" top("));
    }

    [Fact]
    public void BuiltinAsLastGoal_StillWorks_UnderBothLcoSettings()
    {
        // A last goal that is a builtin links as CallBuiltin over the
        // debug_lastcall site (same 9-byte width) when the linker discovers it,
        // and as a compile-time CallBuiltin otherwise. Either way the return
        // stub behind it is the right epilogue.
        var engine = DebugEngine("len(L, N) :- length(L, N).\n");

        Assert.Equal(3, engine.QueryFirst<int>("len([a,b,c], N).", "N"));
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        Assert.Equal(3, engine.QueryFirst<int>("len([a,b,c], N).", "N"));
    }
}
