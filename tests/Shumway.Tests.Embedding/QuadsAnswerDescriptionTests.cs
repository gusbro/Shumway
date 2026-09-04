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
