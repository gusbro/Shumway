using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>How a transcript's answer descriptions are read. A query is
/// recognised by its principal functor alone — <c>Id ?- Goal</c> or
/// <c>?- Goal</c> — and every sentence after it, up to the next query,
/// describes that query's answers. Before, only the FIRST such sentence was
/// taken: the rest reached the compiler, which rejected each as a clause for
/// <c>,/2</c> with a message naming neither the quad nor the file, while the
/// test itself reported a pass on the one description it had read.
///
/// <para>The quad text here is synthetic; the published suites live outside
/// the repo.</para></summary>
public sealed class QuadsAnswerDescriptionTests
{
    private static (PrologEngine Engine, System.IO.StringWriter Out) Loaded()
    {
        var w = new System.IO.StringWriter();
        var e = new PrologEngine { Out = w };
        Assert.True(e.Query("use_module(library(quads)).").Success);
        return (e, w);
    }

    private static string RunQuads(string content)
    {
        var (e, w) = Loaded();
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"quads_ad_{System.Guid.NewGuid():N}.pl");
        System.IO.File.WriteAllText(path, content);
        try
        {
            Assert.True(e.Query($"consult('{path.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("run_quads.").Success);
            return w.ToString();
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void EveryDescriptionSentenceBelongsToItsQuad()
    {
        // Three sentences of description, all of them alternatives of the one
        // query. Only the first used to be read; the other two went to the
        // compiler as clauses for ,/2.
        string report = RunQuads(
            "t1\n?- atom(a).\n" +
            "   false.\n" +
            "   type_error(atom, a).\n" +
            "   true.\n");
        Assert.Contains("quads: 1/1", report);
        Assert.DoesNotContain("not understood", report);
    }

    [Fact]
    public void ADescriptionIsNeverHandedToTheCompiler()
    {
        // The give-away for the old behaviour: a stray description reached
        // the database and was refused there. ,/2 must still be intact and
        // no clause may have been added for it.
        var (e, _) = Loaded();
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"quads_ad_{System.Guid.NewGuid():N}.pl");
        System.IO.File.WriteAllText(path,
            "t1\n?- atom(a).\n   false.\n   true.\n   fails, true.\n");
        try
        {
            Assert.True(e.Query($"consult('{path.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("X = 1, X == 1.").Success);          // ,/2 intact
            Assert.False(e.Query("catch(clause(','(_,_), _), _, fail).").Success);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void AQueryNeedsNoIdentifier()
    {
        // `?- Goal` is a query too: same principal functor rule, one
        // argument. It is reported by its position in the file.
        string report = RunQuads("?- atom(a).\n   true.\n");
        Assert.Contains("quads: 1/1", report);
    }

    [Fact]
    public void AnUnexpectedAlternativeNeverMakesATestPass()
    {
        // `unexpected` marks a wrong answer: it is written down BECAUSE
        // producing it is wrong, so a run that matches it does not pass.
        string report = RunQuads("t1\n?- atom(a).\n   true, unexpected.\n");
        Assert.Contains("quads: 0/1", report);
        Assert.Contains("failing (1): [t1]", report);
    }

    [Fact]
    public void ASanctionedAlternativeBesideAnUnexpectedOneStillPasses()
    {
        string report = RunQuads(
            "t1\n?- atom(a).\n   false, unexpected.\n   true.\n");
        Assert.Contains("quads: 1/1", report);
    }

    [Fact]
    public void ADescriptionItCannotReadIsReportedAgainstItsQuad()
    {
        // The point of the whole exercise: a description written in a
        // vocabulary this harness does not know is NAMED, with its quad,
        // instead of quietly matching whatever happened.
        string report = RunQuads("t1\n?- atom(a).\n   some_word_we_do_not_know.\n");
        Assert.Contains("not understood", report);
        Assert.Contains("t1: some_word_we_do_not_know", report);
        Assert.Contains("quads: 0/1", report);
    }

    [Fact]
    public void AnAnswerSequenceCutShortIsStillReadable()
    {
        // `...` says the answers go on; nothing narrower is claimed, so it
        // must NOT be reported as unreadable.
        string report = RunQuads(
            "t1\n?- member(X, [a,b,c]).\n   X = a ; X = b ; ... .\n");
        Assert.Contains("quads: 1/1", report);
        Assert.DoesNotContain("not understood", report);
    }

    [Theory]
    // inputs(Text) and peeks(Text) say what the goal reads: it must consume
    // the first and leave the second unread. The pair is supplied as one
    // input and BOTH halves are checked, which is what makes a claim about
    // reading testable at all.
    [InlineData("   inputs(\"foo.\"), peeks(\" \"), T = foo.\n", "quads: 1/1")]
    // A wrong claim about what is left over fails.
    [InlineData("   inputs(\"foo.\"), peeks(\"zz\"), T = foo.\n", "quads: 0/1")]
    // ...and so does the same claim marked as a wrong answer.
    [InlineData("   inputs(\"foo.\"), peeks(\" \"), T = foo, unexpected.\n", "quads: 0/1")]
    public void TheInputAndWhatIsLeftOfItAreBothChecked(string description, string expected)
        => Assert.Contains(expected, RunQuads("t1\n?- read(T).\n" + description));

    [Fact]
    public void AQuadWithNoDescriptionAtAllIsCounted()
    {
        // A query whose descriptions never arrived is still a test, and one
        // that can only fail. Vanishing silently is what it must not do.
        string report = RunQuads("t1\n?- atom(a).\n");
        Assert.Contains("quads: 0/1", report);
        Assert.Contains("failing (1): [t1]", report);
    }

    [Theory]
    // What the goal WRITES is compared too. A description claiming one text
    // while the goal prints another describes a different system, and used
    // to pass because the text was taken on trust: the harness printed the
    // real output to the console and reported a pass.
    [InlineData("   outputs(\"z\"), false.\n", "quads: 0/1")]
    [InlineData("   outputs(\"a\"), false.\n", "quads: 1/1")]
    public void WhatTheGoalWritesIsChecked(string description, string expected)
        => Assert.Contains(expected, RunQuads("t1\n?- put_char(a), false.\n" + description));

    [Fact]
    public void AnOutputClaimIsCheckedOnASucceedingGoalToo()
    {
        Assert.Contains("quads: 0/1",
            RunQuads("t1\n?- put_char(a), false ; true.\n   outputs(\"z\").\n"));
        Assert.Contains("quads: 1/1",
            RunQuads("t1\n?- put_char(a), false ; true.\n   outputs(\"a\").\n"));
    }

    [Fact]
    public void AWatchedGoalDoesNotWriteToTheConsole()
    {
        // The output is captured, so a quad's own printing no longer lands
        // in the middle of the report.
        var (e, w) = Loaded();
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"quads_ad_{System.Guid.NewGuid():N}.pl");
        System.IO.File.WriteAllText(path, "t1\n?- put_char(z), true.\n   outputs(\"z\").\n");
        try
        {
            Assert.True(e.Query($"consult('{path.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("run_quads.").Success);
            string report = w.ToString();
            Assert.Contains("quads: 1/1", report);
            Assert.DoesNotContain("z", report);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void ALoopingQuadStillRunsUnderTheTimeLimit()
    {
        // The alternatives carry more than a class now, and a stale pattern
        // for the older shape silently stopped `loops` from being noticed —
        // which turned a 15-second bound into a hang.
        string report = RunQuads("t1\n?- repeat, fail.\n   loops.\n");
        Assert.Contains("quads: 1/1", report);
    }

    [Theory]
    // An error is described by its formal, and the formal is what gets
    // compared. Naming the right kind of error with the wrong culprit
    // describes a different system, and used to pass because only the
    // error's own name was looked at.
    [InlineData("   type_error(integer, a).\n", "quads: 1/1")]
    [InlineData("   type_error(atom, a).\n", "quads: 0/1")]
    [InlineData("   type_error(integer, zzz).\n", "quads: 0/1")]
    [InlineData("   domain_error(integer, a).\n", "quads: 0/1")]
    public void AnErrorIsComparedWholeAndNotByItsKind(string description, string expected)
        => Assert.Contains(expected, RunQuads("t1\n?- atom_length(a, a).\n" + description));

    [Fact]
    public void AnElidedPartOfAnErrorMatchesAnything()
    {
        // `...` stands for a part that was not written down. What IS
        // written still has to agree: the elision is not a blanket pass.
        Assert.Contains("quads: 1/1",
            RunQuads("t1\n?- atom_length(a, a).\n   type_error(integer, ...).\n"));
        Assert.Contains("quads: 0/1",
            RunQuads("t1\n?- atom_length(a, a).\n   type_error(atom, ...).\n"));
        Assert.Contains("quads: 0/1",
            RunQuads("t1\n?- atom(a).\n   type_error(integer, ...).\n"));
    }

    [Fact]
    public void AVariableInADescribedErrorIsAVariableInTheBall()
    {
        // A description names variables where the answer has variables. It
        // is not a wildcard: a described variable against a concrete
        // culprit is a different error.
        Assert.Contains("quads: 1/1",
            RunQuads("t1\n?- atom_length(A, _).\n   instantiation_error.\n"));
        Assert.Contains("quads: 0/1",
            RunQuads("t1\n?- atom_length(a, a).\n   type_error(integer, X).\n"));
    }

    [Fact]
    public void ABallThatIsNotAnErrorIsComparedToo()
    {
        Assert.Contains("quads: 1/1",
            RunQuads("t1\n?- throw(hello).\n   throw(hello).\n"));
        Assert.Contains("quads: 0/1",
            RunQuads("t1\n?- throw(hello).\n   throw(goodbye).\n"));
    }

    [Theory]
    // What the goal ANSWERS is compared, not just that it answered. The
    // description and the query are separate terms, so the L of one is not
    // the L of the other: only their names relate them, and the names come
    // from the source. Without that link all a transcript could say was
    // "the goal succeeds".
    [InlineData("?- length(L, 0).\n   L = [].\n", "quads: 1/1")]
    [InlineData("?- length(L, 0).\n   L = [a].\n", "quads: 0/1")]
    [InlineData("?- X = 1.\n   X = 1.\n", "quads: 1/1")]
    [InlineData("?- X = 1.\n   X = 2.\n", "quads: 0/1")]
    public void TheAnswerItselfIsCompared(string quad, string expected)
        => Assert.Contains(expected, RunQuads("t1\n" + quad));

    [Theory]
    // `;` separates SUCCESSIVE answers, in order. A sequence written down in
    // full claims there are no further answers.
    [InlineData("   X = a ; X = b ; X = c.\n", "quads: 1/1")]
    [InlineData("   X = a ; X = b.\n", "quads: 0/1")]
    [InlineData("   X = b ; X = a ; X = c.\n", "quads: 0/1")]
    [InlineData("   X = a ; X = b ; X = c ; X = d.\n", "quads: 0/1")]
    // ...unless it is left open, which claims only its own prefix.
    [InlineData("   X = a ; X = b ; ... .\n", "quads: 1/1")]
    [InlineData("   X = b ; ... .\n", "quads: 0/1")]
    public void TheAnswersMustBeTheseOnesInThisOrder(string description, string expected)
        => Assert.Contains(expected,
                           RunQuads("t1\n?- member(X, [a,b,c]).\n" + description));

    [Fact]
    public void AnOpenSequenceStillWorksOnAnEndlessGoal()
    {
        // The reason `...` exists: the goal has infinitely many answers, so
        // only as many as are described may be asked for.
        Assert.Contains("quads: 1/1", RunQuads(
            "t1\n?- length(L, N).\n"
            + "   L = [], N = 0 ; L = [_A], N = 1 ; L = [_A,_B], N = 2 ; ... .\n"));
        Assert.Contains("quads: 0/1", RunQuads(
            "t1\n?- length(L, N).\n   L = [], N = 0 ; L = [_A], N = 2 ; ... .\n"));
    }

    [Fact]
    public void AVariableTheDescriptionDoesNotMentionIsUnbound()
    {
        // A top level shows nothing for a variable that stayed unbound, so a
        // description that mentions none is describing an unbound one -- not
        // saying "whatever it is".
        Assert.Contains("quads: 0/1", RunQuads("t1\n?- X = 1, Y = 2.\n   X = 1.\n"));
        Assert.Contains("quads: 1/1",
                        RunQuads("t1\n?- X = 1, Y = 2.\n   X = 1, Y = 2.\n"));
        // A variable whose name starts with an underscore is not shown at
        // all, so a description need not mention it.
        Assert.Contains("quads: 1/1", RunQuads("t1\n?- X = 1, _Y = 2.\n   X = 1.\n"));
    }

    [Fact]
    public void SharingBetweenTheAnswersVariablesIsPartOfTheAnswer()
    {
        // X = f(Y) says X's argument IS Y. A description that renames it
        // describes an answer where the two are independent, which is a
        // different answer.
        Assert.Contains("quads: 1/1", RunQuads("t1\n?- X = f(Y).\n   X = f(Y).\n"));
        Assert.Contains("quads: 0/1", RunQuads("t1\n?- X = f(Y).\n   X = f(Z).\n"));
        Assert.Contains("quads: 1/1", RunQuads("t1\n?- X = Y.\n   X = Y.\n"));
    }

    [Fact]
    public void AFreshVariableInAnAnswerMatchesByRenaming()
    {
        // The answer holds a variable of its own; the name the transcript
        // gives it is not the point, its being a variable is.
        Assert.Contains("quads: 1/1", RunQuads("t1\n?- length(L, 1).\n   L = [_A].\n"));
        Assert.Contains("quads: 0/1", RunQuads("t1\n?- length(L, 1).\n   L = [a].\n"));
    }

    [Fact]
    public void AnAnswerDisplayStillDecidesSuccessAndFailure()
    {
        // Collecting the answers replaces the plain run, so the descriptions
        // that only say true or false keep working alongside one that lists
        // answers.
        Assert.Contains("quads: 1/1",
                        RunQuads("t1\n?- member(X, [a]).\n   X = a\n   |  false.\n"));
        Assert.Contains("quads: 1/1",
                        RunQuads("t1\n?- member(X, []).\n   X = a\n   |  false.\n"));
    }

    [Fact]
    public void WithoutTheSourceTheAnswersAreReportedAsUnchecked()
    {
        // The names live in the file. If it is gone by the time the quads
        // run, the goal is still checked as far as it can be -- and the
        // report says so, rather than passing the weaker check off as a
        // comparison.
        var (e, w) = Loaded();
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"quads_ad_{System.Guid.NewGuid():N}.pl");
        System.IO.File.WriteAllText(path, "z1\n?- length(L, 0).\n   L = [].\n");
        Assert.True(e.Query($"consult('{path.Replace('\\', '/')}').").Success);
        System.IO.File.Delete(path);
        Assert.True(e.Query("run_quads.").Success);
        string report = w.ToString();
        Assert.Contains("quads: 1/1", report);
        Assert.Contains("answers not compared (1): [z1]", report);
    }

    [Fact]
    public void OrdinaryQuadsAreUnaffected()
    {
        // The shapes that already worked keep working: alternatives joined
        // with |, error outcomes, and plain success or failure.
        string report = RunQuads(
            "t1\n?- atom_length(abc, L).\n      L = 3.\n" +
            "t2\n?- atom_length(A, N).\n      instantiation_error.\n" +
            "t3\n?- atom(1).\n      false\n   |  type_error(atom, 1).\n");
        Assert.Contains("quads: 3/3", report);
        Assert.DoesNotContain("not understood", report);
    }
}
