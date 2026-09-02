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
