using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 D5+ — the Watch-window EDIT of a frame variable
/// (<see cref="DebugService.SetFrameVariable"/>), DESTRUCTIVE by design: a bound
/// variable's value is REPLACED (the old binding trailed away, so backtracking restores
/// it), and assigning <c>_</c> UN-instantiates. The Immediate window deliberately keeps
/// pure unification — this surface is the edit gesture only.</summary>
[Collection("debugger")]
public class Adr035WatchEditTests
{
    private readonly ITestOutputHelper _log;
    public Adr035WatchEditTests(ITestOutputHelper log) => _log = log;

    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    // The proof-by-success program: bindit/1 accepts ONLY 7, and the clause binds X to 1
    // first — so the query can only succeed if the edit at the breakpoint really changed
    // the machine.
    //  2: run(Out) :-
    //  3:     X = 1,
    //  4:     bindit(X),
    //  5:     Out = X.
    //  6: bindit(7).
    private const string BinditProgram =
        "run(Out) :-\n    X = 1,\n    bindit(X),\n    Out = X.\nbindit(7).\n";

    private (List<string> Results, List<Solution> Sols) RunEditing(
        PrologEngine engine, string goal, string varName, string newTerm)
    {
        var results = new List<string>();
        int stops = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            if (stops++ == 0)
                results.Add(s.SetFrameVariable(0, varName, newTerm));
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll(goal).ToList();
        engine.AttachDebugSession(null);
        return (results, sols);
    }

    [Fact]
    public void EditingABoundVariable_ReplacesItsValue()
    {
        // X is BOUND to 1 at the stop; the edit replaces it with 7 — pure unification
        // could never do this — and bindit(7) then succeeds.
        var engine = DebugEngine(BinditProgram);
        Assert.True(engine.AddBreakpoint("<string>", 4) > 0);

        var (results, sols) = RunEditing(engine, "run(Out).", "X", "7");

        Assert.Equal(new[] { "" }, results);
        Assert.Single(sols);
        Assert.Equal("7", sols[0]["Out"]!.ToString());
    }

    [Fact]
    public void AssigningUnderscore_Uninstantiates()
    {
        // The user's spec: `_` in the Watch un-binds. X = 1 at the stop; after the edit
        // X is FREE, so bindit(X) binds it to 7 the ordinary way.
        var engine = DebugEngine(BinditProgram);
        Assert.True(engine.AddBreakpoint("<string>", 4) > 0);

        var (results, sols) = RunEditing(engine, "run(Out).", "X", "_");

        Assert.Equal(new[] { "" }, results);
        Assert.Single(sols);
        Assert.Equal("7", sols[0]["Out"]!.ToString());
    }

    [Fact]
    public void TheNewValue_MayAliasSiblingFrameVariables()
    {
        // X := f(Y) where Y is the frame's own Y: the built term references the REAL
        // cell, so when mid/2 binds through the structure, both views agree — the answer
        // shows ONE shared variable in both positions.
        //  2: run(Out) :-
        //  3:     X = 1,
        //  4:     mid(X, Y),
        //  5:     Out = pair(X, Y).
        //  6: mid(f(Z), Z).
        var engine = DebugEngine("""
            run(Out) :-
                X = 1,
                mid(X, Y),
                Out = pair(X, Y).
            mid(f(Z), Z).
            """);
        Assert.True(engine.AddBreakpoint("<string>", 4) > 0);

        var (results, sols) = RunEditing(engine, "run(Out).", "X", "f(Y)");

        Assert.Equal(new[] { "" }, results);
        Assert.Single(sols);
        string outText = sols[0]["Out"]!.ToString()!.Replace(" ", "");
        _log.WriteLine("Out = " + outText);
        // pair(f(V), V) — the SAME variable name twice = real aliasing.
        var m = System.Text.RegularExpressions.Regex.Match(
            outText, @"^pair\(f\((_\w+)\),(_\w+)\)$");
        Assert.True(m.Success, "unexpected shape: " + outText);
        Assert.Equal(m.Groups[1].Value, m.Groups[2].Value);
    }

    [Fact]
    public void BacktrackingPastTheEdit_RestoresTheOriginalBinding()
    {
        // The edit is trailed as-if-the-machine-did-it: backtracking past the edited
        // goal unwinds it. First pass: X = 1 edited to 9 → note(9), answer 9. The redo
        // then unwinds EVERYTHING (edit included) and pick/1's second clause gives the
        // untouched X = 2.
        //  2: :- dynamic(log/1).
        //  3: run(Out) :-
        //  4:     pick(X),
        //  5:     note(X),
        //  6:     Out = X.
        //  7: pick(1).
        //  8: pick(2).
        //  9: note(T) :- assertz(log(T)).
        var engine = DebugEngine("""
            :- dynamic(log/1).
            run(Out) :-
                pick(X),
                note(X),
                Out = X.
            pick(1).
            pick(2).
            note(T) :- assertz(log(T)).
            """);
        Assert.True(engine.AddBreakpoint("<string>", 5) > 0);

        var (results, sols) = RunEditing(engine, "run(Out).", "X", "9");

        Assert.Equal(new[] { "" }, results);
        Assert.Equal(new[] { "9", "2" }, sols.Select(x => x["Out"]!.ToString()).ToArray());
        Assert.Equal(new[] { "9", "2" },
            engine.Query<string>("findall(T, log(T), L), atomic_list_concat(L, ',', A).", "A")
                .Single().Split(','));
    }

    [Fact]
    public void LocalsRenderWriteqStyle_SoTheDisplayRoundTripsThroughTheEdit()
    {
        // The user's report: hola('1234') displayed as hola(1234) — write-style, no
        // quotes — so re-typing the displayed value handed the parser an INTEGER
        // argument. The debugger's displays are writeq-style now: the atom is shown
        // quoted, and pasting the shown text back through the edit preserves the term.
        //  2: run(Out) :-
        //  3:     X = hola('1234'),
        //  4:     check(X),
        //  5:     Out = X.
        //  6: check(hola(A)) :- atom(A).
        var engine = DebugEngine("""
            run(Out) :-
                X = hola('1234'),
                check(X),
                Out = X.
            check(hola(A)) :- atom(A).
            """);
        Assert.True(engine.AddBreakpoint("<string>", 4) > 0);

        string? shown = null;
        var results = new List<string>();
        int stops = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            if (stops++ == 0)
            {
                shown = e.Frames[0].Variables.First(v => v.Name == "X").Value;
                // Round-trip: feed the DISPLAYED text back through the edit.
                results.Add(s.SetFrameVariable(0, "X", shown!));
            }
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll("run(Out).").ToList();
        engine.AttachDebugSession(null);

        _log.WriteLine("Locals showed: " + shown);
        Assert.Equal("hola('1234')", shown);   // writeq-style: the atom is quoted
        Assert.Equal(new[] { "" }, results);
        // check/1 demands atom(A): only succeeds because the round-trip kept the ATOM.
        Assert.Single(sols);
    }

    [Fact]
    public void EditingAnUnknownVariable_IsRefused()
    {
        var engine = DebugEngine(BinditProgram);
        Assert.True(engine.AddBreakpoint("<string>", 4) > 0);

        var (results, sols) = RunEditing(engine, "run(Out).", "Nope", "7");

        Assert.Contains("no variable 'Nope'", results[0]);
        Assert.Empty(sols);   // untouched: bindit(1) fails as it always would
    }
}
