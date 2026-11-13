using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The counters behind the ADR-057 amendment: a multi-clause
/// predicate alone is a region of one member, so its alternatives are pushed
/// inline (no helper push, no IL side-stack entry) and a failure inside it
/// resumes them in the method; a failure outside it resumes them through the
/// interpreter's marker path, never the side stack. Lives here beside
/// <see cref="IlLocalBacktrackCounterTests"/>, in the project CI runs with
/// the diagnostic build.</summary>
[Collection("exclusive")]
public sealed class OneMemberRegionCounterTests(ITestOutputHelper o)
{
    // find/2 fails inside its own first clause; mem/2 is failed into by the
    // query. Both are reached through findall, a meta-call no driver absorbs.
    private const string Corpus = """
        omc_find(X, [X|_]) :- X > 95.
        omc_find(X, [_|T]) :- omc_find(X, T).
        omc_mem(X, [X|_]).
        omc_mem(X, [_|T]) :- omc_mem(X, T).
        """;

    private static (long Pushes, long Local, long Side, long Marker) Run(PrologEngine engine, string goal)
    {
        for (int i = 0; i < 3; i++) Assert.True(engine.Query(goal).Success, goal);
        Assert.True(engine.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        // Clause pushes only: a builtin's push goes through the same helper.
        long pushes = Activation.DiagIlCpPushes - Activation.DiagBuiltinCpPushes, local = Activation.DiagLocalResumes[0];
        long side = Activation.DiagInterpreterIlResumes[0], marker = Activation.DiagInterpreterIlResumes[1];
        Assert.True(engine.Query(goal).Success, goal);
        return (Activation.DiagIlCpPushes - Activation.DiagBuiltinCpPushes - pushes, Activation.DiagLocalResumes[0] - local,
            Activation.DiagInterpreterIlResumes[0] - side, Activation.DiagInterpreterIlResumes[1] - marker);
    }

    [DiagFact]
    public void ARegionOfOnePushesInlineAndResumesItsOwnAlternatives()
    {
        // What is counted is the clause-entry choice point, which find/2's
        // first clause, a head and a test, does not push with head facts on
        // (the test below): they are off here.
        bool headFacts = IlPredicateCompiler.CpFreeHeadFacts;
        IlPredicateCompiler.CpFreeHeadFacts = false;
        try
        {
            var engine = new PrologEngine();
            engine.IlPromotion.Threshold = 1;
            engine.ConsultString(Corpus);

            // 100 elements: 95 guard failures resumed in the method, then the
            // five answers failed into by findall, through the marker path.
            var d = Run(engine, "numlist(1, 100, L), findall(X, omc_find(X, L), Xs), length(Xs, 5).");
            o.WriteLine($"find: pushes={d.Pushes} local={d.Local} side={d.Side} marker={d.Marker}");
            Assert.Equal(0, d.Pushes);
            Assert.True(d.Local >= 95, $"{d.Local} local resumptions");
            Assert.Equal(0, d.Side);

            d = Run(engine, "numlist(1, 100, L), findall(X, omc_mem(X, L), Xs), length(Xs, 100).");
            o.WriteLine($"mem: pushes={d.Pushes} local={d.Local} side={d.Side} marker={d.Marker}");
            Assert.Equal(0, d.Pushes);
            Assert.Equal(0, d.Side);
            Assert.True(d.Marker >= 99, $"{d.Marker} resumptions through the marker path");
        }
        finally
        {
            IlPredicateCompiler.CpFreeHeadFacts = headFacts;
        }
    }

    [DiagFact]
    public void AClauseOfAHeadAndATestPushesNothingWhereItFails()
    {
        // ADR-031 head facts: find/2's first clause runs as a guard with no
        // entry choice point, so its 95 failures go to the next clause with
        // nothing to push or resume; the five matches push theirs, which
        // findall fails into through the marker path.
        Assert.True(IlPredicateCompiler.CpFreeHeadFacts, "head facts are off in this process");
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(Corpus.Replace("omc_", "omh_"));

        var d = Run(engine, "numlist(1, 100, L), findall(X, omh_find(X, L), Xs), length(Xs, 5).");
        o.WriteLine($"find: pushes={d.Pushes} local={d.Local} side={d.Side} marker={d.Marker}");
        Assert.Equal(0, d.Pushes);
        Assert.Equal(0, d.Local);
        Assert.Equal(0, d.Side);
        Assert.True(d.Marker >= 5, $"{d.Marker} resumptions through the marker path");
    }
}
