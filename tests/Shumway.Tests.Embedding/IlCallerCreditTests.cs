using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A predicate called once per query that calls promoted code in a
/// loop promotes: each call from its bytecode into compiled code counts toward
/// it. Counting only its own calls, a generate-and-test driver never reached
/// the threshold, and every iteration crossed from bytecode to IL.</summary>
public sealed class IlCallerCreditTests
{
    // Each driver backtracks inside its own clause, as queens/2 does over
    // permutation/2: the loop's host is the driver, called once per query.
    private const string Corpus = """
        work(X, Y) :- Y is X * 2.
        driver(N, X) :- between(1, N, X), work(X, Y), Y >= 2 * N, !.
        gen(N, X) :- between(1, N, X).
        check(X) :- X mod 7 =:= 0.
        pick(N, X) :- gen(N, X), check(X), X > N - 7, !.
        """;

    private static HashSet<string> Promoted(PrologEngine e)
    {
        var names = new HashSet<string>();
        foreach (int fid in e.IlPromotion.PromotedFunctorIds())
        {
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
            string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
            names.Add($"{name[(name.LastIndexOf('$') + 1)..]}/{arity}");
        }
        return names;
    }

    [Theory]
    [InlineData("driver(300, X), X == 300.", "work/2", "driver/2")]
    [InlineData("pick(300, X), X == 294.", "check/1", "pick/2")]
    public void ARarelyCalledDriverOfPromotedCodePromotes(string goal, string hot, string driver)
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        e.IlPromotion.Threshold = 32;
        e.ConsultString(Corpus);
        var promoted = new HashSet<string>();
        // Five queries: the driver's own calls come nowhere near 32.
        for (int round = 0; round < 5 && !promoted.Contains(driver); round++)
        {
            Assert.True(e.Query(goal).Success, goal);
            e.IlPromotion.WaitForPendingPromotions();
            promoted = Promoted(e);
        }
        // ANTI-VACUITY: the loop body did promote, so the driver called IL.
        Assert.Contains(hot, promoted);
        Assert.True(promoted.Contains(driver),
            $"{driver} did not promote\n{e.IlPromotion.DescribePromotionState()}");
        Assert.True(e.Query(goal).Success, goal);
    }
}
