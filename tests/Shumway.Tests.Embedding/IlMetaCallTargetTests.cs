using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A goal a Tier-1 meta-call runs is dispatched like any call: it
/// counts toward its own promotion and enters its IL once it has one. The
/// meta-call resolves a user goal to its bytecode address, and the dispatch
/// loop used to run that bytecode directly, so a predicate reached only
/// through call/N from promoted code never promoted.</summary>
public sealed class IlMetaCallTargetTests
{
    private const string Corpus = """
        f(X, Y) :- A is X * 3 + 1, Y is A mod 1000 + X.
        g(X) :- X > 2.
        h(X, X).
        drive(_, 0) :- !.
        drive(G, N) :- call(G, N, _), N1 is N - 1, drive(G, N1).
        keep(L, K) :- include(g, L, K).
        sum(G, L, S) :- foldl(add(G), L, 0, S).
        add(G, X, A0, A) :- call(G, X, Y), A is A0 + Y.
        """;

    private static string Name(int fid)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        return $"{name[(name.LastIndexOf('$') + 1)..]}/{arity}";
    }

    [Fact]
    public void APredicateReachedOnlyByAMetaCallFromIlPromotes()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        e.IlPromotion.Threshold = 1;
        e.ConsultString(Corpus);
        // drive/2 promotes first, on another goal: f/2's first call must come
        // from the IL meta-call, or the interpreter's own meta-call counts it.
        var promoted = new HashSet<string>();
        for (int round = 0; round < 5 && !promoted.Contains("drive/2"); round++)
        {
            Assert.True(e.Query("drive(h, 5).").Success);
            e.IlPromotion.WaitForPendingPromotions();
            promoted = e.IlPromotion.PromotedFunctorIds().Select(Name).ToHashSet();
        }
        Assert.Contains("drive/2", promoted);
        Assert.DoesNotContain("f/2", promoted);
        for (int round = 0; round < 5 && !promoted.Contains("f/2"); round++)
        {
            Assert.True(e.Query("drive(f, 20).").Success);
            e.IlPromotion.WaitForPendingPromotions();
            promoted = e.IlPromotion.PromotedFunctorIds().Select(Name).ToHashSet();
        }
        Assert.True(promoted.Contains("f/2"),
            $"f/2 did not promote\n{e.IlPromotion.DescribePromotionState()}");
    }

    [Fact]
    public void MetaCalledGoalsAnswerAsTheInterpreterDoes()
    {
        string[] goals =
        {
            "drive(f, 50).",
            "numlist(1, 6, L), keep(L, K).",
            "numlist(1, 30, L), sum(f, L, S).",
            "findall(X, (member(X, [1,2,3,4]), call(g, X)), L).",
        };
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);
        var tiered = new PrologEngine();
        tiered.IlPromotion.Threshold = 1;
        tiered.ConsultString(Corpus);
        for (int i = 0; i < 3; i++)
        {
            foreach (string g in goals) tiered.Query(g);
            tiered.IlPromotion.WaitForPendingPromotions();
        }
        Assert.Contains("f/2", tiered.IlPromotion.PromotedFunctorIds().Select(Name));

        foreach (string g in goals)
        {
            var a = plain.Query(g);
            var b = tiered.Query(g);
            Assert.Equal(a.Success, b.Success);
            Assert.Equal(
                string.Join(", ", a.Bindings.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {kv.Value}")),
                string.Join(", ", b.Bindings.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {kv.Value}")));
        }
        Assert.True(tiered.Query("numlist(1, 6, L), keep(L, K), K == [3,4,5,6].").Success);
    }
}
