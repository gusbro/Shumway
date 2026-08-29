using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 D5+ — binding free frame variables from the Immediate window.
///
/// <para>The user's design: the goal runs exactly as always (a nested evaluation over the
/// live engine, frame variables substituted), and AFTER a solution its bindings for the
/// frame's free variables are unified INTO the suspended frame — real cells, real
/// trailing, transactionally. If the commit instantiated anything, the parked evaluation's
/// choice points die: <c>;</c> may not walk to another solution the frame is no longer
/// free to take.</para></summary>
[Collection("debugger")]
public class Adr035BindIntoFrameTests
{
    private readonly ITestOutputHelper _log;
    public Adr035BindIntoFrameTests(ITestOutputHelper log) => _log = log;

    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    /// <summary>Runs <paramref name="goal"/>, stops at the breakpoint, evaluates each text
    /// in <paramref name="evals"/> at the stop (frame 0), records each answer, continues to
    /// the end, and returns (answers, solutions).</summary>
    private (List<string> Answers, List<Solution> Solutions) RunAndEvalAtStop(
        PrologEngine engine, string goal, params string[] evals)
    {
        var answers = new List<string>();
        var svc = new DebugService(engine, (s, e) =>
        {
            foreach (string text in evals)
            {
                string a = s.EvaluateGoal(0, text);
                answers.Add(a);
                _log.WriteLine($"?- {text}\n{a}\n");
            }
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll(goal).ToList();
        engine.AttachDebugSession(null);
        return (answers, sols);
    }

    //  2: run(Out) :-
    //  3:     probe(X, Y),
    //  4:     Out = result(X, Y).
    //  5: probe(_, _).
    private const string Program =
        "run(Out) :-\n" +
        "    probe(X, Y),\n" +
        "    Out = result(X, Y).\n" +
        "probe(_, _).\n";

    [Fact]
    public void AUnificationGoal_BindsTheFrameVariable_AndTheProgramSeesIt()
    {
        // Stop on line 3 (the probe call, X and Y still free), bind X from the Immediate
        // window, continue: the PROGRAM's own Out = result(X, Y) sees the binding.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 3) > 0);

        var (answers, sols) = RunAndEvalAtStop(engine, "run(Out).", "X = f(1, hola)");

        // The answer names the committed binding, and says it was committed.
        Assert.Contains("committed to the frame", answers[0]);
        Assert.Contains("X=f(1,hola)", answers[0].Replace(" ", ""));
        Assert.Single(sols);
    }

    [Fact]
    public void ProgramResult_CarriesTheCommittedBinding()
    {
        // The assertion that matters, stated plainly: after binding X at the stop, the
        // program's answer contains f(1, hola) where X was.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 3) > 0);

        var (_, sols) = RunAndEvalAtStop(engine, "run(Out).", "X = f(1, hola)");

        Assert.Single(sols);
        string outText = sols[0]["Out"]!.ToString()!.Replace(" ", "");
        Assert.StartsWith("result(f(1,hola),", outText);
    }

    [Fact]
    public void ASolvingGoal_CommitsItsFirstSolution()
    {
        // Not just =/2: any goal. member(X, [a,b,c]) binds the frame's X to its first
        // solution.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 3) > 0);

        var (answers, sols) = RunAndEvalAtStop(engine, "run(Out).", "member(X, [a, b, c])");

        Assert.Contains("committed to the frame", answers[0]);
        Assert.Single(sols);
        Assert.StartsWith("result(a,", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
    }

    [Fact]
    public void AfterACommit_TheSolutionWalkIsOver()
    {
        // member/2 leaves a choice point; once its first solution committed X, ';' must
        // refuse — the frame is bound to that solution now.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 3) > 0);

        var (answers, _) = RunAndEvalAtStop(engine, "run(Out).",
            "member(X, [a, b, c])", ";");

        Assert.Contains("committed to the frame", answers[0]);
        Assert.Contains("re-solving is disabled", answers[1]);
    }

    [Fact]
    public void WithoutACommit_TheSolutionWalkStaysAvailable()
    {
        // A goal that mentions NO frame variable commits nothing; ';' keeps walking as it
        // always has.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 3) > 0);

        var (answers, _) = RunAndEvalAtStop(engine, "run(Out).",
            "member(Z, [uno, dos])", ";", ";");

        Assert.Contains("Z = uno", answers[0]);
        Assert.DoesNotContain("committed to the frame", answers[0]);
        Assert.Contains("Z = dos", answers[1]);
        Assert.Equal("no more solutions", answers[2]);
    }

    [Fact]
    public void AliasingTwoFrameVariables_Commits()
    {
        // X = Y creates real sharing between two frame cells: binding X later binds Y.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 3) > 0);

        var (answers, sols) = RunAndEvalAtStop(engine, "run(Out).",
            "X = Y", "X = 42");

        Assert.Contains("committed to the frame", answers[0]);
        Assert.Contains("committed to the frame", answers[1]);
        Assert.Single(sols);
        Assert.Equal("result(42,42)", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
    }

    [Fact]
    public void ASharedStructure_BindsBothVariables()
    {
        // X = f(Y): the committed structure EMBEDS the frame's own Y — later binding Y
        // through the program shows inside X. Here: commit X = f(Y), then commit Y = 9;
        // the program's result must be result(f(9), 9).
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 3) > 0);

        var (answers, sols) = RunAndEvalAtStop(engine, "run(Out).",
            "X = f(Y)", "Y = 9");

        Assert.Contains("committed to the frame", answers[0]);
        Assert.Contains("committed to the frame", answers[1]);
        Assert.Single(sols);
        Assert.Equal("result(f(9),9)", sols[0]["Out"]!.ToString()!.Replace(" ", ""));
    }

    [Fact]
    public void AConflictingGoal_FailsAndLeavesTheFrameUntouched()
    {
        // First alias X and Y; then try binding them to DIFFERENT values. The committed
        // aliasing means the second evaluation substitutes BOTH names by the SAME
        // variable — so the conflicting goal simply FAILS in the eval, before any commit
        // is attempted: the frame carries its knowledge into every later evaluation.
        var engine = DebugEngine(Program);
        Assert.True(engine.AddBreakpoint("<string>", 3) > 0);

        var (answers, sols) = RunAndEvalAtStop(engine, "run(Out).",
            "X = Y", "X = 1, Y = 2");

        Assert.Contains("committed to the frame", answers[0]);
        Assert.Equal("false", answers[1]);
        Assert.Single(sols);
        // X = Y survived; 1/2 did not: both stay the SAME free variable in the result.
        string outText = sols[0]["Out"]!.ToString()!.Replace(" ", "");
        var m = System.Text.RegularExpressions.Regex.Match(
            outText, @"^result\((_\w+),(_\w+)\)$");
        Assert.True(m.Success, "expected result(Var, Var), got " + outText);
        Assert.Equal(m.Groups[1].Value, m.Groups[2].Value);   // still aliased
    }

    [Fact]
    public void ABoundFrameVariable_IsSubstitutedNotRebound()
    {
        // Out is FREE at line 3 but X gets bound by the program at line 4 — stop at 4:
        // X is bound, so `X = something_else` substitutes X's VALUE into the goal and
        // simply fails in the eval; the frame does not change.
        //  2: run2(Out) :-
        //  3:     mk(X),
        //  4:     Out = X.
        //  5: mk(hecho).
        var engine = DebugEngine("""
            run2(Out) :-
                mk(X),
                Out = X.
            mk(hecho).
            """);
        Assert.True(engine.AddBreakpoint("<string>", 4) > 0);

        var (answers, sols) = RunAndEvalAtStop(engine, "run2(Out).", "X = otra_cosa");

        Assert.Equal("false", answers[0]);
        Assert.Single(sols);
        Assert.Equal("hecho", sols[0]["Out"]!.ToString());
    }

    [Fact]
    public void ACommittedBinding_IsUndoneByTheProgramsOwnBacktracking()
    {
        // The trailing semantics: the commit behaves as if the program had unified at the
        // stop point. When choice(X) is retried (first solution rejected by test/1), the
        // backtrack unwinds PAST the stop point — the committed binding of W must vanish
        // with it, exactly like a binding the program had made there.
        //  2: run3(Out) :-
        //  3:     choice(X),
        //  4:     mark(X, W),
        //  5:     test(X),
        //  6:     Out = pair(X, W).
        //  7: choice(uno).
        //  8: choice(dos).
        //  9: mark(_, _).
        // 10: test(dos).
        var engine = DebugEngine("""
            run3(Out) :-
                choice(X),
                mark(X, W),
                test(X),
                Out = pair(X, W).
            choice(uno).
            choice(dos).
            mark(_, _).
            test(dos).
            """);
        Assert.True(engine.AddBreakpoint("<string>", 5) > 0);

        int stop = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            stop++;
            if (stop == 1)
            {
                // First stop: X = uno. Bind W; test(uno) will FAIL and backtrack into
                // choice/1 — undoing the commit on the way.
                string a = s.EvaluateGoal(0, "W = marcado");
                _log.WriteLine("stop 1: " + a);
                Assert.Contains("committed to the frame", a);
            }
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        var sols = engine.QueryAll("run3(Out).").ToList();
        engine.AttachDebugSession(null);

        Assert.Equal(2, stop);          // stopped once per choice
        Assert.Single(sols);
        // X = dos survived; W's committed binding was undone by the backtrack — the
        // program ends with W free again.
        string outText = sols[0]["Out"]!.ToString()!.Replace(" ", "");
        Assert.Matches(@"^pair\(dos,_\w+\)$", outText);
    }
}
