using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-057's counter: a Tier-1 region resumes its own choice points
/// and leaves any other one to the interpreter. It lives here, not beside the
/// Tier-1 equivalence tests in Embedding, because this is the project CI runs
/// with the diagnostic build; elsewhere the counter compiles to nothing and the
/// test would only ever skip.</summary>
[Collection("exclusive")]
public sealed class IlLocalBacktrackCounterTests(ITestOutputHelper o)
{
    private const string Corpus = """
        pick(1). pick(2). pick(3).
        bt(0) :- !.
        bt(N) :- pick(X), X == 3, N1 is N - 1, bt(N1).
        vcall(N) :- N > 0, G = pick(X), findall(X, call(G), _), N1 is N - 1, vcall(N1).
        vcall(0).
        """;

    private static long[] Run(PrologEngine engine, string goal)
    {
        for (int i = 0; i < 3; i++) engine.Query(goal);
        engine.IlPromotion.WaitForPendingPromotions();
        Array.Clear(Activation.DiagLocalResumes);
        Assert.True(engine.Query(goal).Success, goal);
        return (long[])Activation.DiagLocalResumes.Clone();
    }

    [DiagFact]
    public void ARegionResumesItsOwnChoicePointsAndNoOther()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(Corpus);

        var d = Run(engine, "bt(1000).");
        o.WriteLine($"bt: resumed={d[0]} floor={d[1]} other={d[2]} debug={d[3]}");
        // pick/1 answers 1 and 2 before 3: two resumptions per iteration, all local.
        Assert.Equal(2000, d[0]);
        Assert.Equal(0, d[2]);

        // A goal called through a variable pushes its choice points outside
        // the region: the region declines them and the interpreter resumes.
        d = Run(engine, "vcall(100).");
        o.WriteLine($"vcall: resumed={d[0]} floor={d[1]} other={d[2]} debug={d[3]}");
        Assert.True(d[2] >= 100, $"{d[2]} declined: a choice point the region did not push was resumed in it");
    }
}
