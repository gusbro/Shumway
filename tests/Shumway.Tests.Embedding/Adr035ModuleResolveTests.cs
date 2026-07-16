using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 — the Immediate window resolves module-local predicates. Stopped inside a
/// module's own code, a goal typed by its source name (`show_usage`) reaches the module-local
/// predicate (`blint$show_usage`), and an explicit `Module:Goal` works too — the user does not
/// have to know the mangled name.</summary>
public class Adr035ModuleResolveTests
{
    private readonly ITestOutputHelper _log;
    public Adr035ModuleResolveTests(ITestOutputHelper log) => _log = log;

    //  1: :- module(blint).
    //  2: :- public(run/0).
    //  3: run :- helper.
    //  4: helper :-
    //  5:     marker.            <- break here; the frame is blint$helper -> module "blint"
    //  6: marker.
    //  7: show_usage :- true.
    //  8: greet(hello).
    //  9: greet(world).
    private const string Source =
        ":- module(blint).\n:- public(run/0).\nrun :- helper.\nhelper :-\n    marker.\n"
        + "marker.\nshow_usage :- true.\ngreet(hello).\ngreet(world).\n";

    private static PrologEngine DebugEngine()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + Source);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    /// <summary>Break at line 5 (inside blint$helper), run each goal, collect the results.</summary>
    private List<string> EvalAtBreak(PrologEngine engine, params string[] goals)
    {
        var results = new List<string>();
        bool done = false;
        var svc = new DebugService(engine, (s, e) =>
        {
            if (done) return;
            done = true;
            foreach (var g in goals) results.Add(s.EvaluateGoal(0, g));
        });
        engine.AttachDebugSession(svc);
        engine.AddBreakpoint("<string>", 5);
        engine.QueryAll("run.").ToList();
        engine.AttachDebugSession(null);
        _log.WriteLine(string.Join(" | ", results));
        return results;
    }

    [Fact]
    public void AnUnqualifiedLocalPredicate_ResolvesAgainstTheStoppedFramesModule()
    {
        var engine = DebugEngine();
        // `show_usage` is local to module blint; stopped inside blint$helper, it resolves.
        Assert.Equal(new[] { "true" }, EvalAtBreak(engine, "show_usage"));
    }

    [Fact]
    public void AnExplicitModuleQualifiedGoal_Resolves()
    {
        var engine = DebugEngine();
        Assert.Equal(new[] { "true" }, EvalAtBreak(engine, "blint:show_usage"));
    }

    [Fact]
    public void AModuleTypedWithADotPlSuffix_IsForgiven()
    {
        // The Call Stack may show the module with a ".pl"; typing it that way (quoted, since a
        // dotted atom must be) still resolves to the real module `blint`.
        var engine = DebugEngine();
        Assert.Equal(new[] { "true" }, EvalAtBreak(engine, "'blint.pl':show_usage"));
    }

    [Fact]
    public void ALocalPredicateWithSolutions_BacktracksWithSemicolon()
    {
        var engine = DebugEngine();
        Assert.Equal(
            new[] { "W = hello", "W = world", "no more solutions" },
            EvalAtBreak(engine, "greet(W)", ";", ";"));
    }

    [Fact]
    public void AGlobalOrBuiltinGoal_StillResolves_NotMisMangled()
    {
        var engine = DebugEngine();
        // member/2 is a global prelude predicate: there is no blint$member, so it stays global.
        Assert.Equal(new[] { "E = a", "E = b" }, EvalAtBreak(engine, "member(E, [a,b])", ";"));
    }

    [Fact]
    public void AGenuinelyUndefinedPredicate_StillErrors()
    {
        var engine = DebugEngine();
        var r = EvalAtBreak(engine, "no_such_pred")[0];
        Assert.Contains("existence_error", r);
    }
}
