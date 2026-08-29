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
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
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
        e.ConsultString("""
            :- dynamic flag/1.
            :- assertz(flag(set)).
            """);
        Assert.True(e.Query("flag(set).").Success);
    }

    [Fact]
    public void UndefinedDirective_Warns_ButDoesNotAbortTheConsult()
    {
        var e = new PrologEngine();
        string err = CaptureStderr(() =>
            // The `use_module` typo the fix targets: a warning, not silence,
            // and p/1 still loads.
            e.ConsultString("""
                :- use(library(coroutining)).
                p(ok).
                """));

        Assert.Contains("use/1", err);
        Assert.True(e.Query("p(X).")["X"]!.ToString() == "ok");
    }

    [Fact]
    public void FailingDirective_Warns_ButDoesNotAbortTheConsult()
    {
        var e = new PrologEngine();
        string err = CaptureStderr(() =>
            e.ConsultString("""
                :- fail.
                q(ok).
                """));

        Assert.Contains("directive failed", err);
        Assert.True(e.Query("q(ok).").Success);
    }

    [Fact]
    public void GoalDirective_RunsAfterCommit_SeeingThisFilesPredicates()
    {
        // A `:- main.`-style directive at EOF calls a predicate defined in the
        // same file. It runs post-commit, so the predicate is visible.
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic done/0.
            setup :- assertz(done).
            :- setup.
            """);
        Assert.True(e.Query("done.").Success);
    }

    [Fact]
    public void GoalDirective_RunsBeforeInitializationGoal()
    {
        // ISO: general directives run during load; initialization/1 after it.
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic ev/1.
            :- assertz(ev(directive)).
            :- initialization(assertz(ev(init))).
            """);
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
            e.ConsultString("""
                :- op(700, xfx, ===).
                r(ok).
                """));
        Assert.DoesNotContain("directive", err);
        Assert.True(e.Query("r(ok).").Success);
    }

    [Fact]
    public void ABigDirectiveIsNotReportedInFull()
    {
        // A foreign-interface declaration from another system names hundreds of
        // functions. Rendered whole it is a single line of tens of kilobytes,
        // which is not a diagnostic anyone reads — the head is what identifies
        // which directive it was, and the error already says what went wrong.
        var args = string.Join(", ", Enumerable.Range(0, 400).Select(i => $"fn_{i}(ptr, sint)"));
        var e = new PrologEngine();
        string err = CaptureStderr(() =>
            e.ConsultString($":- bind_them(libc, [{args}]).\np(ok)."));

        string line = err.Split('\n').First(l => l.Contains("directive raised"));
        Assert.True(line.Length < 200, $"still {line.Length} characters");
        Assert.Contains("bind_them(libc, ...)", line);
        Assert.Contains("bind_them/2", line);            // the error names it
        Assert.True(e.Query("p(ok).").Success);
    }

    [Fact]
    public void ASmallDirectiveIsReportedWhole()
    {
        // The elision is for the ones nobody can read. An ordinary directive
        // still says exactly what it was.
        var e = new PrologEngine();
        string err = CaptureStderr(() => e.ConsultString(":- nope(a, b)."));
        Assert.Contains("nope(a, b)", err);
    }

    [Fact]
    public void AModuleDirectiveIsReportedAsQualified()
    {
        // Inside a module the consult wraps the goal to run it in that module's
        // context. The wrapper is machinery, not something the user wrote, so
        // the warning reads as the M:G it stands for.
        var e = new PrologEngine();
        string err = CaptureStderr(() => e.ConsultString("""
            :- module(m, []).
            :- nope(a).
            """));
        Assert.Contains("m:nope(a)", err);
        Assert.DoesNotContain("$mqual", err);
    }

    [Fact]
    public void MultipleGoalDirectives_RunInSourceOrder()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic log/1.
            :- assertz(log(a)).
            :- assertz(log(b)).
            """);
        var order = e.QueryAll("log(X).").Select(s => s["X"]!.ToString()).ToList();
        Assert.Equal(new[] { "a", "b" }, order);
    }
}
