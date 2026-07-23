using System;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ISO §7.4.2 — a directive `:- G` that is not one of the recognised
/// declaration directives is a goal, executed during loading (in source
/// order, before any initialization/1 goal). A goal that fails or raises
/// warns and loading continues; it does not abort the consult.
/// </summary>
public sealed class DirectiveGoalTests
{
    private static string CaptureStderr(Action a)
    {
        var prev = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { a(); } finally { Console.SetError(prev); }
        return sw.ToString();
    }

    [Fact]
    public void GeneralGoalDirective_Runs_WithSideEffectVisible()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic flag/1.\n:- assertz(flag(set)).\n");
        Assert.True(e.Query("flag(set).").Success);
    }

    [Fact]
    public void UndefinedDirective_Warns_ButDoesNotAbortTheConsult()
    {
        var e = new PrologEngine();
        string err = CaptureStderr(() =>
            // The `use_module` typo the fix targets: a warning, not silence,
            // and p/1 still loads.
            e.ConsultString(":- use(library(coroutining)).\np(ok).\n"));

        Assert.Contains("use/1", err);
        Assert.True(e.Query("p(X).")["X"]!.ToString() == "ok");
    }

    [Fact]
    public void FailingDirective_Warns_ButDoesNotAbortTheConsult()
    {
        var e = new PrologEngine();
        string err = CaptureStderr(() =>
            e.ConsultString(":- fail.\nq(ok).\n"));

        Assert.Contains("directive failed", err);
        Assert.True(e.Query("q(ok).").Success);
    }

    [Fact]
    public void GoalDirective_RunsAfterCommit_SeeingThisFilesPredicates()
    {
        // A `:- main.`-style directive at EOF calls a predicate defined in the
        // same file. It runs post-commit, so the predicate is visible.
        var e = new PrologEngine();
        e.ConsultString(
            ":- dynamic done/0.\n" +
            "setup :- assertz(done).\n" +
            ":- setup.\n");
        Assert.True(e.Query("done.").Success);
    }

    [Fact]
    public void GoalDirective_RunsBeforeInitializationGoal()
    {
        // ISO: general directives run during load; initialization/1 after it.
        var e = new PrologEngine();
        e.ConsultString(
            ":- dynamic ev/1.\n" +
            ":- assertz(ev(directive)).\n" +
            ":- initialization(assertz(ev(init))).\n");
        var order = e.QueryAll("ev(X).").Select(s => s["X"]!.ToString()).ToList();
        Assert.Equal(new[] { "directive", "init" }, order);
    }

    [Fact]
    public void RecognisedDeclaration_DoesNotWarn()
    {
        // op/3 is applied at parse time; reaching the general-directive branch
        // must not warn or re-run it as a failing goal.
        var e = new PrologEngine();
        string err = CaptureStderr(() =>
            e.ConsultString(":- op(700, xfx, ===).\nr(ok).\n"));
        Assert.DoesNotContain("directive", err);
        Assert.True(e.Query("r(ok).").Success);
    }

    [Fact]
    public void MultipleGoalDirectives_RunInSourceOrder()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- dynamic log/1.\n" +
            ":- assertz(log(a)).\n" +
            ":- assertz(log(b)).\n");
        var order = e.QueryAll("log(X).").Select(s => s["X"]!.ToString()).ToList();
        Assert.Equal(new[] { "a", "b" }, order);
    }
}
