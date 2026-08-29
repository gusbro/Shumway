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
[Collection("debugger")]
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
        => EvalAtLine(engine, 5, goals);

    /// <summary>Break at <paramref name="line"/>, run each goal from the innermost frame.</summary>
    private List<string> EvalAtLine(PrologEngine engine, int line, params string[] goals)
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
        engine.AddBreakpoint("<string>", line);
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
    public void StoppedInAPublicPredicate_StillResolvesTheModulesLocals()
    {
        // `run/0` (the `:- set_prolog_flag` prepend shifts it to line 4) is PUBLIC — compiled
        // global, no module prefix of its own. The single-module program still resolves
        // `show_usage` against blint (the sole user module), as a user stopped anywhere in Blint
        // expects.
        var engine = DebugEngine();
        Assert.Equal(new[] { "true" }, EvalAtLine(engine, 4, "show_usage"));
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
    public void AtTheEntryBreak_StoppedInPublicMain_ResolvesTheModulesLocals()
    {
        // The user's exact scenario: attach, the entry break stops at `main` (public), and a bare
        // `show_usage` typed in the Immediate window must resolve — before any F11 steps into a
        // local predicate.
        var src =
            ":- module(blint).\n:- public(main/0).\nmain :- helper.\n"
            + "helper :- true.\nshow_usage :- true.\n";
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + src);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

        string result = "";
        var svc = new DebugService(engine, (_, _) => { });
        svc.EntryBreak = _ => { result = svc.EvaluateGoal(0, "show_usage"); };
        engine.AttachDebugSession(svc);
        svc.ArmEntryBreak();
        engine.QueryAll("main.").ToList();
        engine.AttachDebugSession(null);

        _log.WriteLine("entry eval: " + result);
        Assert.Equal("true", result);
    }

    [Fact]
    public void AtTheEntryBreak_OnTheBundleLoadPath_ResolvesTheModulesLocals()
    {
        // The real --exe path: a Debuggable bundle whose module is "Blint" (the file base name,
        // no `:- module` directive), a PUBLIC main, and a local show_usage. Stopped at the entry
        // break in main, a bare `show_usage` must resolve.
        var engine = new PrologEngine();
        engine.Flags.EmitDebugInfo = true;
        engine.Flags.DebugCodegen = true;
        engine.LoadBundle(new Bundle(new[]
        {
            new BundleEntry("Blint",
                ":- public main/0.\n" +
                "main :- helper.\n" +
                "helper :- true.\n" +
                "show_usage :- true.\n"),
        }));
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

        string result = "";
        var svc = new DebugService(engine, (_, _) => { });
        svc.EntryBreak = _ => { result = svc.EvaluateGoal(0, "show_usage"); };
        engine.AttachDebugSession(svc);
        svc.ArmEntryBreak();
        engine.QueryAll("main.").ToList();
        engine.AttachDebugSession(null);

        _log.WriteLine("bundle entry eval: " + result);
        Assert.Equal("true", result);
    }

    [Fact]
    public void MultiModule_EntryBreakInPublicMain_ResolvesViaTheUniqueDefiningModule()
    {
        // TWO modules, so neither the frame's own prefix (public main), nor the source file
        // (ConsultString is "<string>"), nor the single-module shortcut applies — the module can
        // NOT be pinned. `show_usage` is defined in exactly ONE module, so it still resolves; a
        // name defined in neither stays undefined.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- set_prolog_flag(compile_mode, debug).
            :- module(other).
            :- public(o/0).
            o :- true.
            secondary :- true.
            """);
        engine.ConsultString("""
            :- module(blint).
            :- public(main/0).
            main :- helper.
            helper :- true.
            show_usage :- true.
            """);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

        string usage = "", missing = "";
        var svc = new DebugService(engine, (_, _) => { });
        svc.EntryBreak = _ =>
        {
            usage = svc.EvaluateGoal(0, "show_usage");     // unique to blint
            missing = svc.EvaluateGoal(0, "not_anywhere"); // in no module
        };
        engine.AttachDebugSession(svc);
        svc.ArmEntryBreak();
        engine.QueryAll("main.").ToList();
        engine.AttachDebugSession(null);

        _log.WriteLine($"usage={usage} missing={missing}");
        Assert.Equal("true", usage);
        Assert.Contains("existence_error", missing);
    }

    [Fact]
    public void TwoModulesDefineTheSameName_ResolvesToTheStoppedFramesModule()
    {
        // `tag/1` is local to BOTH alpha and beta, so the unique-module fallback is ambiguous —
        // the only thing that can pick one is the FRAME's module, taken from the call-stack line's
        // file (alpha.pl). Stopped in alpha's amain, `tag(R)` must be alpha's.
        var engine = new PrologEngine();
        engine.Flags.EmitDebugInfo = true;
        engine.Flags.DebugCodegen = true;
        engine.LoadBundle(new Bundle(new[]
        {
            new BundleEntry("alpha",
                ":- public amain/0.\namain :- ahelper.\nahelper :- true.\ntag(from_alpha).\n"),
            new BundleEntry("beta",
                ":- public bmain/0.\nbmain :- true.\ntag(from_beta).\n"),
        }));
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

        string tag = "";
        var svc = new DebugService(engine, (_, _) => { });
        svc.EntryBreak = _ => { tag = svc.EvaluateGoal(0, "tag(R)"); };
        engine.AttachDebugSession(svc);
        svc.ArmEntryBreak();
        engine.QueryAll("amain.").ToList();
        engine.AttachDebugSession(null);

        _log.WriteLine("tag=" + tag);
        Assert.Equal("R = from_alpha", tag);
    }

    [Fact]
    public void AGenuinelyUndefinedPredicate_StillErrors()
    {
        var engine = DebugEngine();
        var r = EvalAtBreak(engine, "no_such_pred")[0];
        Assert.Contains("existence_error", r);
    }
}
