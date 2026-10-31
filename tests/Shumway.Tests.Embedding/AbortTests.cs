using System;
using System.IO;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>abort/0</c> abandons the running query, SICStus' way: catch/3
/// never sees it, the cleanups of the setup_call_cleanup scopes it leaves
/// still run, and it unwinds nested queries up to the outermost one, whose
/// caller gets <see cref="PrologAbortException"/>.</summary>
public class AbortTests
{
    private static PrologEngine Engine(string program = "")
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic(seen/1). :- dynamic(cleaned/1).\n" + program);
        return engine;
    }

    [Theory]
    [InlineData(@"abort")]
    [InlineData(@"catch(abort, _, assertz(seen(any)))")]
    [InlineData(@"catch(abort, E, assertz(seen(E)))")]
    [InlineData(@"findall(X, (member(X, [1, 2]), abort), _)")]
    [InlineData(@"\+ abort")]
    [InlineData(@"call((true, abort))")]
    public void TheHostGetsTheAbortAndCatchNeverSeesIt(string goal)
    {
        var engine = Engine();

        Assert.Throws<PrologAbortException>(() => engine.Query(goal + "."));
        Assert.False(engine.Query("seen(_).").Success);
    }

    [Fact]
    public void TheCleanupsOfTheScopesItLeavesRunInnermostFirst()
    {
        var engine = Engine();

        Assert.Throws<PrologAbortException>(() => engine.Query(@"
            setup_call_cleanup(true,
                setup_call_cleanup(true, abort, assertz(cleaned(inner))),
                assertz(cleaned(outer)))."));
        Assert.True(engine.Query("findall(X, cleaned(X), L), L == [inner, outer].").Success);
    }

    [Fact]
    public void TheEngineAnswersTheNextQuery()
    {
        var engine = Engine("p(1). p(2).");

        Assert.Throws<PrologAbortException>(() => engine.Query("p(_), abort."));
        Assert.True(engine.Query("findall(X, p(X), L), L == [1, 2].").Success);
    }

    [Fact]
    public void AnAbortInACompiledPredicateReachesTheHost()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(@"
            :- dynamic(cleaned/1).
            down(0) :- abort.
            down(N) :- N > 0, N1 is N - 1, down(N1).
            run :- setup_call_cleanup(true, down(50), assertz(cleaned(yes))).
            ");

        Assert.Throws<PrologAbortException>(() => engine.Query("run."));
        Assert.True(engine.Query("cleaned(yes).").Success);
    }

    [Theory]
    [InlineData(@":- initialization(abort).")]
    [InlineData(@":- abort.")]
    public void AnAbortWhileConsultingEndsTheConsult(string directive)
    {
        var engine = Engine();

        Assert.Throws<PrologAbortException>(() => engine.ConsultString(directive + "\nafter."));
    }

    [Theory]
    // A hook loaded before the file it expands.
    [InlineData(@"term_expansion(stop, _) :- abort.", @"stop.")]
    [InlineData(@"goal_expansion(stop, _) :- abort.", @"p :- stop.")]
    // A hook in the same file, applied to a directive after it.
    [InlineData(@"", "term_expansion((:- stop), _) :- abort.\n:- stop.")]
    public void AnAbortInAnExpansionHookEndsTheConsult(string hooks, string source)
    {
        var engine = Engine(hooks);

        Assert.Throws<PrologAbortException>(() => engine.ConsultString(source));
    }

    // Every top level renders what a query raised through this; an abort is
    // not an error, and the stack of the last one is not its.
    [Fact]
    public void ATopLevelReportsItAsAbortedNotAsAnError()
    {
        var engine = Engine();
        Assert.ThrowsAny<Exception>(() => engine.Query("atom_length(1, a)."));

        Assert.Equal(new[] { "Execution aborted." },
            Shumway.TopLevel.ErrorRendering.Describe(engine, new PrologAbortException()));
    }

    [Fact]
    public void AnAbortInAFileConsultedByAQueryEndsTheQuery()
    {
        string file = Path.Combine(Path.GetTempPath(), "abort_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(file, ":- initialization(abort).\n");
        try
        {
            var engine = Engine();
            string path = file.Replace("\\", "/");

            Assert.Throws<PrologAbortException>(() =>
                engine.Query($"consult('{path}'), assertz(seen(after))."));
            Assert.False(engine.Query("seen(_).").Success);
        }
        finally { File.Delete(file); }
    }
}
