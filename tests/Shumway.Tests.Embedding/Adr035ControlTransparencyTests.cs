using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 (Camino B) — control-construct transparency in the debugger.
///
/// <para>A pure control construct (<c>,</c> / <c>;</c> / <c>-&gt;</c> and the runtime
/// <c>$call_*</c> dispatch plumbing) is flow, not a goal: standard Prolog tracers keep it
/// transparent, so stepping goes straight from a clause to the real user goals. But the
/// all-solutions meta-predicates (<c>findall</c> / <c>bagof</c> / <c>setof</c> / <c>forall</c>)
/// lower to the SAME <c>;</c> / <c>\+</c> collect loop — yet they ARE goals the user wrote and
/// must stop / show as themselves. MetaTransform tags their collect-loop helper with the
/// meta-predicate's kind so the two are told apart.</para></summary>
[Collection("debugger")]
public class Adr035ControlTransparencyTests
{
    private readonly ITestOutputHelper _log;
    public Adr035ControlTransparencyTests(ITestOutputHelper log) => _log = log;

    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    private List<DebugStopEvent> Walk(PrologEngine engine, int bpLine, string goal, StepMode mode)
    {
        Assert.True(engine.AddBreakpoint("<string>", bpLine) > 0, $"bp at line {bpLine}");
        var stops = new List<DebugStopEvent>();
        var svc = new DebugService(engine, (s, e) => { stops.Add(e); s.Resume(mode); });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll(goal).ToList();
        engine.AttachDebugSession(null);
        foreach (var s in stops)
        {
            string frames = string.Join(" | ", s.Frames.Select(f => $"{f.Name}/{f.Arity}@{f.Line}"));
            _log.WriteLine($"{s.Reason,-13} goal={s.Goal,-16} d={s.Depth} {s.File}:{s.Line}  frames=[{frames}]");
        }
        return stops;
    }

    // The prepended `:- set_prolog_flag(compile_mode, debug).` is line 1, so a program's
    // own lines are all shifted by one throughout.

    [Fact]
    public void StepIntoAUserWrapperCall_DescendsIntoItsDefinition_WithNoControlFrames()
    {
        //  2: top(A) :-
        //  3:     ifthenelse(cond(A), (used(A), more(A)), other(A)).
        //  4: ifthenelse(X,Y,Z):-
        //  5:     X->Y;Z.
        //  6: cond(1). 7: used(1). 8: more(1). 9: other(0).
        var engine = DebugEngine("""
            top(A) :-
                ifthenelse(cond(A), (used(A), more(A)), other(A)).
            ifthenelse(X,Y,Z):-
                X->Y;Z.
            cond(1).
            used(1).
            more(1).
            other(0).
            """);
        var stops = Walk(engine, bpLine: 3, "top(A).", StepMode.Into);

        // The meta-wrapper unfold is off under debug, so the call to ifthenelse/3 stays a real
        // call and the walk descends into the wrapper's own clause.
        Assert.Contains(stops, s => s.Frames.Any(f => f.Name == "ifthenelse" && f.Arity == 3));
        // Its `X->Y;Z` body and the runtime dispatch of the variable branches are transparent:
        // we stop on the real user goals...
        Assert.Contains(stops, s => s.Goal is "cond/1" or "used/1" or "more/1");
        // ...and never on, or under a frame of, a control construct or its lowered plumbing.
        Assert.DoesNotContain(stops, s =>
            s.Goal is ";/2" or ",/2" or "->/2" || s.Goal.StartsWith("$disj_") || s.Goal.StartsWith("$call"));
        Assert.DoesNotContain(stops, s => s.Frames.Any(f =>
            f.Name is "," or ";" or "->" or "*->" || f.Name.StartsWith("$disj_") || f.Name.StartsWith("$call")));
    }

    [Fact]
    public void APlainUserDisjunction_IsTransparent_SteppingLandsOnTheBranchGoal()
    {
        //  2: pick(X) :-
        //  3:     first,
        //  4:     ( a(X) ; b(X) ).
        //  5: first. 6: a(1). 7: b(2).
        // The breakpoint sits on `first` (line 3), a goal BEFORE the disjunction, so the
        // branch goals' call ports are not deduped against the breakpoint's own site.
        var engine = DebugEngine("""
            pick(X) :-
                first,
                ( a(X) ; b(X) ).
            first.
            a(1).
            b(2).
            """);
        var stops = Walk(engine, bpLine: 3, "pick(X).", StepMode.Into);

        // Step into the disjunction and land on the first branch's goal, not on a ;/2, and
        // never surface the disjunction as a stop or a frame (Call OR Redo).
        Assert.Contains(stops, s => s.Goal == "a/1");
        Assert.DoesNotContain(stops, s => s.Goal is ";/2" || s.Goal.StartsWith("$disj_"));
        Assert.DoesNotContain(stops, s => s.Frames.Any(f => f.Name is ";" || f.Name.StartsWith("$disj_")));
    }

    [Fact]
    public void Findall_IsAVisibleMetaPredicate_StopsAndShowsAsFindall_NotADisjunction()
    {
        //  2: run(Xs) :-
        //  3:     findall(X, item(X), Xs).
        //  4: item(1). 5: item(2).
        var engine = DebugEngine("""
            run(Xs) :-
                findall(X, item(X), Xs).
            item(1).
            item(2).
            """);
        var stops = Walk(engine, bpLine: 3, "run(Xs).", StepMode.Into);

        // findall lowers to a `;` collect loop, but it IS a goal the user wrote: it stops at its
        // own line and shows as findall/3 — never leaking the ;/2 or $disj_N it compiled to.
        Assert.Contains(stops, s => s.Goal == "findall/3" && s.Line == 3);
        Assert.DoesNotContain(stops, s => s.Goal is ";/2" || s.Goal.StartsWith("$disj_"));
        // The user's own enumerated goal inside it is still reached.
        Assert.Contains(stops, s => s.Goal == "item/1");
    }
}
