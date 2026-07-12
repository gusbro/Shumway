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
/// property of a predicate, not of a module, and a module can hand the debugger
/// the predicates worth stepping through while the rest keeps release codegen.
///
/// <para>A non-debuggable predicate carries no stop sites, no forced frames and no
/// runtime-switchable last call — it is one opaque step. Crucially it stays
/// COHERENT: a debuggable predicate it calls is debugged normally, because the
/// two compile independently and the environment chain runs through both.</para>
/// </summary>
public class Adr035DisableDebugTests
{
    private readonly ITestOutputHelper _log;

    public Adr035DisableDebugTests(ITestOutputHelper log) => _log = log;

    private sealed class SiteRecorder : IDebugSession
    {
        public readonly List<int> Lines = new();
        public void OnBreak(Activation e, int siteId) => Lines.Add(DebugSiteTable.Get(siteId).Line);
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

    private List<int> SitesHit(string program, string goal, out int solutions)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        var rec = new SiteRecorder();
        engine.AttachDebugSession(rec);
        solutions = engine.QueryAll(goal).Count();
        engine.AttachDebugSession(null);
        _log.WriteLine("stop sites on lines: " + string.Join(", ", rec.Lines));
        return rec.Lines;
    }

    [Fact]
    public void DisableDebug_SilencesTheClausesThatFollowIt()
    {
        //   2: visible_a(1).
        //   3: :- disable_debug.
        //   4: hidden_b(2).
        var lines = SitesHit(
            "visible_a(1).\n:- disable_debug.\nhidden_b(2).\n",
            "visible_a(X), hidden_b(Y).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Equal(new[] { 2 }, lines);   // hidden_b/1 contributes nothing
    }

    [Fact]
    public void EnableDebug_TurnsItBackOn_ForTheRest()
    {
        //   2: :- disable_debug.
        //   3: hidden(1).
        //   4: :- enable_debug.
        //   5: visible(2).
        var lines = SitesHit(
            ":- disable_debug.\nhidden(1).\n:- enable_debug.\nvisible(2).\n",
            "hidden(X), visible(Y).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Equal(new[] { 5 }, lines);
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
        var lines = SitesHit(
            "a(1).\n:- disable_debug.\nb(2).\n:- enable_debug.\nc(3).\n"
            + ":- disable_debug.\nd(4).\n",
            "a(_), b(_), c(_), d(_).", out int solutions);

        Assert.Equal(1, solutions);
        Assert.Equal(new[] { 2, 6 }, lines);
    }

    [Fact]
    public void AnOpaquePredicate_CallingADebuggableOne_ResumesNormalDebugging()
    {
        // The coherence requirement. opaque/1 is a black box — no stop sites of
        // its own — but the predicate it calls is debugged frame by frame, because
        // the two are compiled independently and the machine's environment chain
        // runs through both regardless.
        //   2: :- disable_debug.
        //   3: opaque(X) :- deep(X).
        //   4: :- enable_debug.
        //   5: deep(X) :- leaf(X).
        //   6: leaf(42).
        var lines = SitesHit(
            ":- disable_debug.\nopaque(X) :- deep(X).\n:- enable_debug.\n"
            + "deep(X) :- leaf(X).\nleaf(42).\n",
            "opaque(A).", out int solutions);

        Assert.Equal(1, solutions);
        // Nothing from opaque/1 (line 3); everything from deep/1 and leaf/1.
        Assert.DoesNotContain(3, lines);
        Assert.Equal(new[] { 5, 5, 6 }, lines);   // deep's entry, its body goal, leaf's entry
    }

    [Fact]
    public void OpaquePredicates_KeepReleaseCodegen_AndTheLcoFlagDoesNotTouchThem()
    {
        // No debug_lastcall is emitted for them, so debug_lco is inert there: the
        // whole point is that they compile exactly as release code does.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- set_prolog_flag(compile_mode, debug).\n"
            + ":- disable_debug.\n"
            + "fast(X) :- helper(X).\n"
            + "helper(5).\n");
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

        // Still correct, and still tail-calling: a deep tail recursion in an
        // opaque predicate runs in constant stack even with LCO "off".
        engine.ConsultString(
            ":- disable_debug.\n"
            + "loop(0, 0).\nloop(N, R) :- N > 0, M is N - 1, loop(M, R).\n");

        Assert.Equal(5, engine.QueryFirst<int>("fast(X).", "X"));
        Assert.Equal(0, engine.QueryFirst<int>("loop(50000, R).", "R"));
    }

    [Fact]
    public void TheDirectivesAreInertWithoutDebugCompilation()
    {
        // In a release build they mean nothing — there was no debug codegen to
        // switch off — and above all they must not change the program.
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
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- set_prolog_flag(compile_mode, debug).\n"
            + "rev([], []).\nrev([H|T], R) :- rev(T, RT), app(RT, [H], R).\n"
            + ":- disable_debug.\n"
            + "app([], L, L).\napp([H|T], L, [H|R]) :- app(T, L, R).\n");

        Assert.Equal(new[] { 3, 2, 1 }, engine.QueryFirst<List<int>>("rev([1,2,3], R).", "R"));
    }
}
