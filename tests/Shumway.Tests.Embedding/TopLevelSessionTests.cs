using System;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The shared top level (<see cref="TopLevelSession"/>), extracted from the REPL so
/// the console and the browser front-end drive the same logic. These pin the
/// behaviour that used to be reachable only by running the REPL and reading its
/// output: pull-based solutions, residual-constraint formatting, the
/// does-not-parse fallback, cancellation, and completion.
/// </summary>
public class TopLevelSessionTests
{
    private static TopLevelSession NewSession(string? program = null)
    {
        var engine = new PrologEngine { Out = new StringWriter() };
        var session = new TopLevelSession(engine);
        if (program is not null) session.Consult(program);
        return session;
    }

    [Fact]
    public void FormatsABinding()
    {
        using var run = NewSession().StartQuery("X = hello.");
        Assert.True(run.Parsed);
        Assert.True(run.MoveNext());
        Assert.Equal("X = hello", run.Format(80));
    }

    [Fact]
    public void AGoalWithNoVariablesFormatsAsTrue()
    {
        using var run = NewSession().StartQuery("atom(foo).");
        Assert.True(run.MoveNext());
        Assert.Equal("true", run.Format(80));
    }

    [Fact]
    public void PullsSolutionsOneAtATime()
    {
        using var run = NewSession().StartQuery("member(X, [a,b,c]).");
        var seen = new System.Collections.Generic.List<string>();
        while (run.MoveNext()) seen.Add(run.Format(80));
        Assert.Equal(new[] { "X = a", "X = b", "X = c" }, seen);
    }

    [Fact]
    public void AFailingGoalYieldsNoSolution()
    {
        using var run = NewSession().StartQuery("fail.");
        Assert.False(run.MoveNext());
    }

    [Fact]
    public void ReportsTheLastSolution()
    {
        using var run = NewSession().StartQuery("member(X, [only]).");
        Assert.True(run.MoveNext());
        Assert.True(run.IsLast);
    }

    [Fact]
    public void UnparseableTextStillRuns_SoTheEngineReportsIt()
    {
        // The engine, not the session, reports a syntax error: the session hands
        // the raw text to the engine, whose string-form query parses eagerly, so
        // the parser's diagnostic surfaces from StartQuery with its position
        // intact. That is what the REPL always did — it catches it and renders it.
        var session = NewSession();
        var ex = Assert.Throws<Shumway.Compiler.Parsing.ParseException>(
            () => session.StartQuery("this is not( prolog."));
        Assert.Contains("1:20", ex.Message);
    }

    [Fact]
    public void ExposesTheQueryVariablesInSourceOrder()
    {
        using var run = NewSession().StartQuery("X = 1, Y = 2, Z = 3.");
        Assert.Equal(new[] { "X", "Y", "Z" }, run.UserVariables.ToArray());
    }

    [Fact]
    public void ProjectsResidualConstraints()
    {
        // The reason the top level wraps a goal in copy_term/3 at all: an answer
        // that is constrained but unbound must read as its constraint, not as a
        // blank. Without the wrapping this prints nothing for A.
        var engine = new PrologEngine { Out = new StringWriter() };
        engine.UseClpfd();
        using var run = new TopLevelSession(engine).StartQuery("A #> 5, A #< 10.");
        Assert.True(run.MoveNext());
        Assert.Equal("A in 6..9", run.Format(80));
    }

    [Fact]
    public void ProjectsResidualConstraintsOfVariablesInsideATerm()
    {
        // The queens shape: the constrained variables are ELEMENTS of a list the
        // goal built, not query variables. Their domains must still be reported —
        // an answer of `Qs = [_G4, _G6, _G8]` alone says nothing true about Qs.
        var engine = new PrologEngine { Out = new StringWriter() };
        engine.UseClpfd();
        using var run = new TopLevelSession(engine)
            .StartQuery("length(Qs, 3), Qs ins 1..3.");
        Assert.True(run.MoveNext());
        string answer = run.Format(80);

        var lines = answer.Split(",\n");
        Assert.Equal(4, lines.Length);              // the binding + one domain each
        Assert.StartsWith("Qs = [", lines[0]);
        // And each domain names a variable the binding actually shows.
        foreach (string line in lines.Skip(1))
        {
            Assert.EndsWith(" in 1..3", line);
            Assert.Contains(line[..line.IndexOf(' ')], lines[0]);
        }
    }

    [Fact]
    public void AVariableInsideAnotherValuePrintsAsItsName()
    {
        // `Y = f(_G0)` next to `_G0 in 4..6` reads as two unrelated facts. The
        // name the user typed is the one thing tying the answer together.
        var engine = new PrologEngine { Out = new StringWriter() };
        engine.UseClpfd();
        using var run = new TopLevelSession(engine)
            .StartQuery("X #> 3, X #< 7, Y = f(X).");
        Assert.True(run.MoveNext());
        Assert.Equal("X in 4..6,\nY = f(X)", run.Format(80));
    }

    [Fact]
    public void SharedValuesChainInsteadOfRepeating()
    {
        // SWI-style: A = B, B = value — not the same value printed twice.
        using var run = NewSession().StartQuery("X = Y, Y = shared.");
        Assert.True(run.MoveNext());
        Assert.Equal("X = Y,\nY = shared", run.Format(80));
    }

    [Fact]
    public void CancelStopsASearch()
    {
        using var run = NewSession().StartQuery("between(1, 100000000, X), X > 99999999.");
        run.Cancel();
        Assert.Throws<OperationCanceledException>(() => run.MoveNext());
    }

    [Fact]
    public void ConsultedPredicatesAreSolvable()
    {
        var session = NewSession("anc(X,Y) :- par(X,Y).  anc(X,Z) :- par(X,Y), anc(Y,Z).  par(a,b).  par(b,c).");
        using var run = session.StartQuery("anc(a, X).");
        var seen = new System.Collections.Generic.List<string>();
        while (run.MoveNext()) seen.Add(run.Format(80));
        Assert.Equal(new[] { "X = b", "X = c" }, seen);
    }

    [Fact]
    public void CompletionFindsBuiltinsAndUserPredicates()
    {
        var session = NewSession("zzz_user_predicate(1).");
        var matches = session.Complete("zzz_user_");
        Assert.Contains("zzz_user_predicate", matches);

        Assert.Contains("append", session.Complete("appen"));
        Assert.Empty(session.Complete("no_such_predicate_prefix_xyzzy"));
    }

    [Fact]
    public void CompletionIsSortedAndDeduplicated()
    {
        var matches = NewSession().Complete("at");
        Assert.Equal(matches.OrderBy(m => m, StringComparer.Ordinal), matches);
        Assert.Equal(matches.Distinct().Count(), matches.Count);
    }

    // ---- variables the user named with a leading underscore ----
    // "_A" says the caller is not asking about that one, so its VALUE is not
    // part of the answer. Every expectation here is SWI's, measured.

    [Fact]
    public void AnUnderscoreNamedVariablesBindingIsNotReported()
    {
        using var run = NewSession().StartQuery("_A = 5.");
        Assert.True(run.MoveNext());
        Assert.Equal("true", run.Format(80));
    }

    [Fact]
    public void OnlyTheUnderscoreNamedBindingIsDropped()
    {
        using var run = NewSession().StartQuery("X = 1, _B = 2.");
        Assert.True(run.MoveNext());
        Assert.Equal("X = 1", run.Format(80));
    }

    [Fact]
    public void ItIsTheSubjectThatDecidesNotTheValue()
    {
        // Naming the variable inside a value is what makes the answer readable,
        // so an underscore name still PRINTS there — the asymmetry is SWI's:
        // `X = _A` reports, `_A = X` does not.
        using var reported = NewSession().StartQuery("X = f(_A).");
        Assert.True(reported.MoveNext());
        Assert.Equal("X = f(_A)", reported.Format(80));

        using var aliasShown = NewSession().StartQuery("X = _A.");
        Assert.True(aliasShown.MoveNext());
        Assert.Equal("X = _A", aliasShown.Format(80));

        using var aliasHidden = NewSession().StartQuery("_A = X.");
        Assert.True(aliasHidden.MoveNext());
        Assert.Equal("true", aliasHidden.Format(80));
    }

    [Fact]
    public void ResidualsOfAnUnderscoreNamedVariableAreStillReported()
    {
        // The point of the exercise: what such a variable is CONSTRAINED to is
        // an answer even though what it is bound to is not.
        var engine = new PrologEngine { Out = new StringWriter() };
        engine.UseClpfd();
        using var run = new TopLevelSession(engine).StartQuery("_A #> 5, _A #< 10.");
        Assert.True(run.MoveNext());
        Assert.Equal("_A in 6..9", run.Format(80));
    }

    [Fact]
    public void ABindingIsDroppedWhileItsNeighboursResidualSurvives()
    {
        var engine = new PrologEngine { Out = new StringWriter() };
        engine.UseClpfd();
        using var run = new TopLevelSession(engine)
            .StartQuery("_A = 5, X = 3, _B #> 7.");
        Assert.True(run.MoveNext());
        Assert.Equal("X = 3,\n_B in 8..sup", run.Format(80));
    }

    // ---- the keys offered once an answer is on screen (issue #30) ----

    [Theory]
    [InlineData(';', MoreAnswers.One)]
    [InlineData(' ', MoreAnswers.One)]
    [InlineData('n', MoreAnswers.One)]
    [InlineData('a', MoreAnswers.All)]
    [InlineData('f', MoreAnswers.Chunk)]
    [InlineData('h', MoreAnswers.Help)]
    [InlineData('.', MoreAnswers.Stop)]
    [InlineData('\r', MoreAnswers.Stop)]
    [InlineData('q', MoreAnswers.Stop)]
    public void EachKeyMeansOneThing(char key, MoreAnswers expected)
        => Assert.Equal(expected, AnswerPrompt.KeyMeans(key));

    [Fact]
    public void TabAsksForOneMoreWhateverItsCharacterIs()
        => Assert.Equal(MoreAnswers.One, AnswerPrompt.KeyMeans('\t', isTab: true));

    [Fact]
    public void FFillsOutTheCurrentGroupOfFiveRatherThanAlwaysFive()
    {
        // Five is a BOUNDARY, so the blocks stay aligned however you got there:
        // after one answer `f` brings four, after five it brings five.
        Assert.Equal(4, AnswerPrompt.ChunkAfter(1));
        Assert.Equal(3, AnswerPrompt.ChunkAfter(2));
        Assert.Equal(1, AnswerPrompt.ChunkAfter(4));
        Assert.Equal(5, AnswerPrompt.ChunkAfter(5));
        Assert.Equal(5, AnswerPrompt.ChunkAfter(10));
        Assert.Equal(2, AnswerPrompt.ChunkAfter(13));
        // Whatever it is asked, it never asks for nothing — a key that took no
        // answers would read as a dead keypress.
        for (int shown = 0; shown < 40; shown++)
        {
            int chunk = AnswerPrompt.ChunkAfter(shown);
            Assert.InRange(chunk, 1, 5);
            Assert.Equal(0, (shown + chunk) % 5);   // lands on a boundary
        }
    }

    [Fact]
    public void PressingFShowsFourMoreAndThenAsksAgain()
    {
        // From one answer, `f` must land the reader on the fifth — not the
        // fourth, and not the sixth. This is the count the console cannot be
        // asked about in a test, so it is asked here.
        var pacer = new AnswerPrompt.Pacer();
        Assert.True(pacer.AskAfterShowing());                 // answer 1: ask
        Assert.True(pacer.Accept(MoreAnswers.Chunk));         // ...they press f
        Assert.False(pacer.AskAfterShowing());                // 2
        Assert.False(pacer.AskAfterShowing());                // 3
        Assert.False(pacer.AskAfterShowing());                // 4
        Assert.True(pacer.AskAfterShowing());                 // 5: ask again
        Assert.Equal(5, pacer.Shown);

        // And from a boundary it brings a full five.
        Assert.True(pacer.Accept(MoreAnswers.Chunk));
        for (int i = 0; i < 4; i++) Assert.False(pacer.AskAfterShowing());
        Assert.True(pacer.AskAfterShowing());
        Assert.Equal(10, pacer.Shown);
    }

    [Fact]
    public void PressingAStopsAskingAltogether()
    {
        var pacer = new AnswerPrompt.Pacer();
        Assert.True(pacer.AskAfterShowing());
        Assert.True(pacer.Accept(MoreAnswers.All));
        for (int i = 0; i < 50; i++) Assert.False(pacer.AskAfterShowing());
        Assert.Equal(51, pacer.Shown);
    }

    [Fact]
    public void AnythingElseEndsTheEnumeration()
    {
        var pacer = new AnswerPrompt.Pacer();
        Assert.True(pacer.AskAfterShowing());
        Assert.False(pacer.Accept(MoreAnswers.Stop));
    }

    [Fact]
    public void TheHelpListsEveryKeyThatDoesSomething()
    {
        string help = AnswerPrompt.Help;
        foreach (string key in new[] { ";", "SPACE", "n", "a", "f", "h", "RETURN" })
            Assert.Contains(key, help);
    }

    [Fact]
    public void OutputGoesToTheWriterTheHostSupplied()
    {
        var sink = new StringWriter();
        var session = new TopLevelSession(new PrologEngine { Out = sink });
        using var run = session.StartQuery("write(from_the_session), nl.");
        Assert.True(run.MoveNext());
        Assert.Equal("from_the_session", sink.ToString().Trim());
    }
}
