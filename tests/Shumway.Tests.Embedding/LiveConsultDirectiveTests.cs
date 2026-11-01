using System;
using System.IO;
using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A file consulted by a running goal (consult/1 and friends) runs its
/// <c>:- Goal</c> directives and <c>initialization/1</c> goals on that goal's
/// activation, as a call placed after the consult, not as a query of its own
/// per goal (whose setup recompiled the whole program each time). The outcomes
/// stay those of a load: a failing or raising goal warns and the load goes on,
/// halt ends it, and nothing of a goal (a cut, a choice point) reaches the
/// consulting query.
/// </summary>
public sealed class LiveConsultDirectiveTests
{
    private static string WriteTemp(string text)
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_livedir_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(path, text);
        return path.Replace('\\', '/');
    }

    [Fact]
    public void TheGoalsOfAConsultedFileRunInOrderAndSeeItsPredicates()
    {
        string f = WriteTemp("""
            setup(X) :- X = from_the_file.
            :- setup(X), assertz(ev(directive(X))).
            :- initialization((setup(X), assertz(ev(init(X))))).
            """);
        try
        {
            var e = new PrologEngine();
            e.ConsultString(":- dynamic ev/1.");
            var sol = e.Query(
                $"consult('{f}'), findall(E, ev(E), Es), "
                + "Es == [directive(from_the_file), init(from_the_file)].");
            Assert.True(sol.Success);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void TheGoalsOfConsultedFilesDoNotSetTheProgramUpAgain()
    {
        const int files = 6;
        var paths = new string[files];
        for (int i = 0; i < files; i++)
            paths[i] = WriteTemp($"p{i}(X) :- X = {i}.\n:- initialization(assertz(seen({i}))).\n");
        try
        {
            var e = new PrologEngine();
            e.ConsultString(":- dynamic seen/1.");
            long before = e.ProgramProductBuilds;
            string consults = string.Join(", ", paths.Select(p => $"consult('{p}')"));
            var sol = e.Query(
                $"{consults}, findall(I, seen(I), Is), Is == [0, 1, 2, 3, 4, 5], p5(5).");
            Assert.True(sol.Success);
            // The query's own setup and no other: each initialization goal ran
            // on the query's activation. Run as a query of its own, each set the
            // changed program up again, one build more per file.
            Assert.Equal(1, e.ProgramProductBuilds - before);
        }
        finally { foreach (var p in paths) File.Delete(p); }
    }

    [Fact]
    public void AFailingGoalWarnsAndTheLoadAndTheQueryGoOn()
    {
        string f = WriteTemp("""
            :- fail.
            :- initialization(fail).
            after(loaded).
            """);
        try
        {
            var warnings = new StringWriter();
            var e = new PrologEngine { Warnings = warnings };
            var sol = e.Query($"consult('{f}'), after(X).");
            Assert.True(sol.Success);
            Assert.Equal(new AtomTerm("loaded"), sol["X"]);
            string w = warnings.ToString();
            Assert.Contains("directive failed: fail", w);
            Assert.Contains("initialization goal failed: fail", w);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void ARaisingGoalWarnsWithItsBallAndTheLoadGoesOn()
    {
        string f = WriteTemp("""
            :- use(library(coroutining)).
            :- initialization(throw(my_ball)).
            after(loaded).
            """);
        try
        {
            var warnings = new StringWriter();
            var e = new PrologEngine { Warnings = warnings };
            var sol = e.Query($"consult('{f}'), after(X).");
            Assert.True(sol.Success);
            Assert.Equal(new AtomTerm("loaded"), sol["X"]);
            string w = warnings.ToString();
            Assert.Contains("directive raised", w);
            Assert.Contains("use/1", w);
            Assert.Contains("initialization goal raised", w);
            Assert.Contains("my_ball", w);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void AHaltingGoalEndsTheLoadAndTheQueryWithItsExitCode()
    {
        string f = WriteTemp(":- initialization(halt(3)).\n");
        try
        {
            var e = new PrologEngine();
            e.ConsultString(":- dynamic after_consult/0.");
            var sols = e.QueryAll($"consult('{f}'), assertz(after_consult).").ToList();
            Assert.Empty(sols);
            Assert.Equal(3, e.LastHaltExitCode);
            Assert.False(e.Query("after_consult.").Success);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void ACutOrAChoicePointOfAGoalDoesNotReachTheConsultingQuery()
    {
        string f = WriteTemp("""
            :- !.
            :- member(_, [a, b, c]).
            :- initialization(member(_, [x, y])).
            """);
        try
        {
            var e = new PrologEngine();
            // The cut does not commit the query's member/2, and the goals'
            // choice points add no solution (they would multiply the two).
            var xs = e.QueryAll($"member(X, [1, 2]), consult('{f}').")
                .Select(s => s["X"]).ToList();
            Assert.Equal(new Term[] { new IntTerm(1), new IntTerm(2) }, xs);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void TheGoalsOfAModuleFileRunInThatModule()
    {
        string f = WriteTemp("""
            :- module(live_dir_m, [answer/1]).
            :- dynamic recorded/1.
            local(X) :- X = inside.
            :- local(X), assertz(recorded(directive(X))).
            :- initialization((local(X), assertz(recorded(init(X))))).
            answer(Xs) :- findall(X, recorded(X), Xs).
            """);
        try
        {
            var e = new PrologEngine();
            var sol = e.Query(
                $"consult('{f}'), live_dir_m:answer(Xs), "
                + "Xs == [directive(inside), init(inside)].");
            Assert.True(sol.Success);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void AGoalMayConsultAnotherFileWhoseGoalsRunToo()
    {
        string inner = WriteTemp(":- dynamic nested/1.\n:- assertz(nested(yes)).\n");
        string outer = WriteTemp($":- initialization(consult('{inner}')).\n");
        try
        {
            var e = new PrologEngine();
            var sol = e.Query($"consult('{outer}'), nested(X).");
            Assert.True(sol.Success);
            Assert.Equal(new AtomTerm("yes"), sol["X"]);
        }
        finally { File.Delete(inner); File.Delete(outer); }
    }
}
