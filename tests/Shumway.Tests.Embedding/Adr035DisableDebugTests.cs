using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 phase D1 — <c>:- disable_debug.</c> / <c>:- enable_debug.</c>. The two
/// directives are POSITIONAL: each sets the debuggability of the clauses that
/// follow it, until the next one or the end of the file. So debuggability is a
/// property of a predicate, not of a module, and a file can hand the debugger the
/// predicates worth stepping through while the rest keeps release codegen.
///
/// <para>A non-debuggable predicate records no stop sites, so no breakpoint binds
/// inside it — it is one opaque step. Crucially it stays COHERENT: a debuggable
/// predicate it calls is debugged normally, because the two compile independently
/// and the machine's environment chain runs through both.</para>
/// </summary>
public class Adr035DisableDebugTests
{
    private readonly ITestOutputHelper _log;

    public Adr035DisableDebugTests(ITestOutputHelper log) => _log = log;

    private sealed class Recorder : IDebugSession
    {
        // net10 defaults these on the interface; net48 has no default
        // interface implementations, so the explicit no-ops are required.
        void IDebugSession.OnInlineGoal(Activation engine) { }
        void IDebugSession.OnLeaveProlog(Activation engine) { }

        private readonly PrologEngine _engine;
        public readonly List<int> Lines = new();
        public Recorder(PrologEngine engine) => _engine = engine;

        public void OnBreak(Activation e, int pc) =>
            Lines.Add(DebugSiteTable.Get(_engine.SiteAt(pc)).Line);

        public void OnCallAddress(Activation e, int a, bool t) { }
        public void OnCallFunctor(Activation e, int f, bool t) { }
        public void OnCallBuiltin(Activation e, int b, bool t) { }
        public void OnBuiltinResult(Activation e, int b, bool ok) { }
        public void OnExit(Activation e) { }
        public void OnRedo(Activation e, int pc) { }
        public void OnFail(Activation e) { }
        public void MarkHeapRoots(Action<int> mark) { }
        public void RelocateHeapRoots(
            Shumway.Core.Activation engine, Func<int, int> reloc, Func<int, int> relocBoundary) { }
    }

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
    public void NoBreakpointBinds_InsideADisabledRegion()
    {
        //   2: visible_a(1).
        //   3: :- disable_debug.
        //   4: hidden_b(2).
        var engine = DebugEngine("visible_a(1).\n:- disable_debug.\nhidden_b(2).\n");

        Assert.True(engine.AddBreakpoint("<string>", 2) > 0);   // debuggable
        Assert.Equal(0, engine.AddBreakpoint("<string>", 4));   // opaque — cannot bind
    }

    [Fact]
    public void EnableDebug_TurnsItBackOn_ForTheRest()
    {
        //   2: :- disable_debug.
        //   3: hidden(1).
        //   4: :- enable_debug.
        //   5: visible(2).
        var engine = DebugEngine(
            ":- disable_debug.\nhidden(1).\n:- enable_debug.\nvisible(2).\n");

        Assert.Equal(0, engine.AddBreakpoint("<string>", 3));
        Assert.True(engine.AddBreakpoint("<string>", 5) > 0);

        var hits = HitsFor(engine, "hidden(X), visible(Y).", out int solutions);
        Assert.Equal(1, solutions);
        Assert.Equal(new[] { 5 }, hits);
    }

    [Fact]
    public void ARegionCanBeOpenedAndClosedSeveralTimesInOneFile()
    {
        //   2: a(1).                <- debuggable
        //   3: :- disable_debug.
        //   4: b(2).                <- not
        //   5: :- enable_debug.
        //   6: c(3).                <- debuggable
        //   7: :- disable_debug.
        //   8: d(4).                <- not
        var engine = DebugEngine(
            "a(1).\n:- disable_debug.\nb(2).\n:- enable_debug.\nc(3).\n"
            + ":- disable_debug.\nd(4).\n");

        Assert.True(engine.AddBreakpoint("<string>", 2) > 0);
        Assert.Equal(0, engine.AddBreakpoint("<string>", 4));
        Assert.True(engine.AddBreakpoint("<string>", 6) > 0);
        Assert.Equal(0, engine.AddBreakpoint("<string>", 8));

        var hits = HitsFor(engine, "a(_), b(_), c(_), d(_).", out int solutions);
        Assert.Equal(1, solutions);
        Assert.Equal(new[] { 2, 6 }, hits);
    }

    [Fact]
    public void AnOpaquePredicate_CallingADebuggableOne_ResumesNormalDebugging()
    {
        // The coherence requirement. opaque/1 is a black box — nothing binds inside
        // it — but the predicate it calls is debugged normally, because the two are
        // compiled independently and the environment chain runs through both either
        // way.
        //   2: :- disable_debug.
        //   3: opaque(X) :- deep(X).
        //   4: :- enable_debug.
        //   5: deep(X) :- leaf(X).
        //   6: leaf(42).
        var engine = DebugEngine(
            ":- disable_debug.\nopaque(X) :- deep(X).\n:- enable_debug.\n"
            + "deep(X) :- leaf(X).\nleaf(42).\n");

        Assert.Equal(0, engine.AddBreakpoint("<string>", 3));   // inside the black box
        Assert.True(engine.AddBreakpoint("<string>", 6) > 0);   // beyond it

        var hits = HitsFor(engine, "opaque(A).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Equal(new[] { 6 }, hits);   // stopped in leaf/1, reached THROUGH the opaque call
    }

    [Fact]
    public void OpaquePredicates_KeepReleaseCodegen()
    {
        // No debug_lastcall is emitted for them, so debug_lco is inert there: a deep
        // tail recursion in an opaque predicate keeps running in constant stack even
        // with LCO switched "off".
        var engine = DebugEngine(
            ":- disable_debug.\n"
            + "loop(0, 0).\nloop(N, R) :- N > 0, M is N - 1, loop(M, R).\n");
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

        Assert.Equal(0, engine.QueryFirst<int>("loop(50000, R).", "R"));
    }

    [Fact]
    public void TheDirectivesAreInertWithoutDebugCompilation()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            "a(1).\n:- disable_debug.\nb(2).\n:- enable_debug.\nc(3).\n");

        Assert.Equal(1, engine.QueryFirst<int>("a(X).", "X"));
        Assert.Equal(2, engine.QueryFirst<int>("b(X).", "X"));
        Assert.Equal(3, engine.QueryFirst<int>("c(X).", "X"));
    }

    [Fact]
    public void ProgramMeaningIsUnchanged_WhicheverRegionsAreDisabled()
    {
        var engine = DebugEngine(
            "rev([], []).\nrev([H|T], R) :- rev(T, RT), app(RT, [H], R).\n"
            + ":- disable_debug.\n"
            + "app([], L, L).\napp([H|T], L, [H|R]) :- app(T, L, R).\n");

        Assert.Equal(new[] { 3, 2, 1 }, engine.QueryFirst<List<int>>("rev([1,2,3], R).", "R"));
    }
}
