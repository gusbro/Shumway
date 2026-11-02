using System.IO;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An assert or retract of a fact, between queries, leaves the
/// compiled program product in place: the persistent buffer already holds
/// the mutation (ADR-015), and the next setup leaves the mutated predicate's
/// compile in the product as it was, for the next full build. Rebuilding the
/// whole product after every such query cost O(program), and compiling the
/// predicate alone cost its size per query and built the term of every fact
/// kept as an image (ADR-063). A rule keeps the full build: its rewrite can
/// mint helpers the next query has to link.</summary>
public sealed class ProgramProductReuseTests
{
    private const string Program = """
        :- dynamic(f/1).
        :- dynamic(g/2).
        f(0).
        stat(X) :- X > 0.
        """;

    private static PrologEngine Engine(int threshold)
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(Program);
        e.IlPromotion.Threshold = threshold;
        Assert.True(e.Query("f(0).").Success);
        return e;
    }

    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FactsAssertedAndRetractedAcrossQueriesKeepTheProduct(int threshold)
    {
        var e = Engine(threshold);
        long builds = e.ProgramProductBuilds;
        for (int i = 1; i <= 5; i++)
            Assert.True(e.Query($"assertz(f({i})).").Success);
        Assert.True(e.Query("findall(X, f(X), L), L == [0, 1, 2, 3, 4, 5].").Success);
        Assert.True(e.Query("retract(f(2)), retract(f(4)).").Success);
        Assert.True(e.Query("findall(X, f(X), L), L == [0, 1, 3, 5].").Success);
        // A predicate first given clauses, then emptied, then given one back.
        Assert.True(e.Query("assertz(g(a, 1)), assertz(g(b, 2)).").Success);
        Assert.True(e.Query("g(b, X), X == 2.").Success);
        Assert.True(e.Query("retract(g(_, _)), retract(g(_, _)).").Success);
        Assert.False(e.Query("g(_, _).").Success);
        Assert.True(e.Query("asserta(g(c, 3)).").Success);
        Assert.True(e.Query("findall(K-V, g(K, V), L), L == [c-3].").Success);
        Assert.True(e.Query("stat(1).").Success);
        Assert.Equal(builds, e.ProgramProductBuilds);
    }

    [Fact]
    public void ARuleAssertedRebuildsTheProductAndRuns()
    {
        var e = Engine(0);
        long builds = e.ProgramProductBuilds;
        Assert.True(e.Query("assertz((g(X, Y) :- ( X > 1 -> Y = big ; Y = small ))).").Success);
        Assert.True(e.Query("g(2, Y), Y == big, g(0, Z), Z == small.").Success);
        Assert.True(e.ProgramProductBuilds > builds, "an asserted rule kept a product that cannot link its helpers");
    }

    // The setup after an assert does not compile the predicate: a fact kept
    // as an image stays one, its term never built, through the queries that
    // add to the table, read it by key and retract from it. A full build
    // reads the terms.
    [Fact]
    public void FactsAssertedAcrossQueriesStayImagesUntilAFullBuildReadsThem()
    {
        var e = Engine(0);
        long builds = e.ProgramProductBuilds;
        for (int i = 1; i <= 20; i++)
            Assert.True(e.Query($"assertz(g({i}, v({i}))).").Success);
        Assert.True(e.Query("g(7, X), X == v(7), findall(K, g(K, _), L), length(L, 20).").Success);
        Assert.True(e.Query("retract(g(3, _)), \\+ g(3, _).").Success);
        Assert.True(e.Query("g(20, V), V == v(20).").Success);
        Assert.Equal(builds, e.ProgramProductBuilds);
        var clauses = e.DynamicClausesFor(Fid("g", 2));
        Assert.Equal(19, clauses.Count);
        Assert.All(clauses, c => { Assert.NotNull(c.HeadImage); Assert.False(c.HasTerm); });
        Assert.True(e.Query("assertz((g(X, Y) :- X == rule, Y = yes)).").Success);
        Assert.True(e.Query("g(rule, A), A == yes, g(7, B), B == v(7).").Success);
        Assert.True(e.ProgramProductBuilds > builds);
        Assert.All(e.DynamicClausesFor(Fid("g", 2)), c => Assert.True(c.HasTerm));
    }

    // A predicate the setup left as it was is still promoted: the snapshot
    // has it rewritten for itself, and sees every fact; a mutation evicts the
    // snapshot and the next one sees the new facts.
    [Fact]
    public void APredicateLeftAsItWasIsPromotedWithAllItsFacts()
    {
        var e = Engine(1);
        e.IlPromotion.BackgroundCompilation = false;
        int fid = Fid("g", 2);
        long builds = e.ProgramProductBuilds;
        for (int i = 1; i <= 5; i++)
            Assert.True(e.Query($"assertz(g({i}, {i * i})).").Success);
        long rewrites = e.DeferredDynamicRewrites;
        Assert.True(e.Query("forall(between(1, 5, I), (g(I, S), S =:= I * I)), \\+ g(6, _).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.True(e.IlPromotion.IsPromoted(fid), "a predicate the setup left as it was never got its snapshot");
        Assert.Equal(1, e.DeferredDynamicRewrites - rewrites);
        Assert.True(e.Query("assertz(g(6, 36)).").Success);
        Assert.False(e.IlPromotion.IsPromoted(fid));
        Assert.True(e.Query("forall(between(1, 6, I), (g(I, S), S =:= I * I)), \\+ g(7, _).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.True(e.IlPromotion.IsPromoted(fid));
        Assert.Equal(2, e.DeferredDynamicRewrites - rewrites);
        Assert.Equal(builds, e.ProgramProductBuilds);
    }

    // Once its rule is retracted, the predicate is one of facts again and the
    // product stays through the mutations that follow.
    [Fact]
    public void ARetractedRuleLeavesAPredicateOfFacts()
    {
        var e = Engine(0);
        Assert.True(e.Query("assertz(g(a, 1)), assertz((g(X, Y) :- Y = X)).").Success);
        Assert.True(e.Query("g(a, 1), g(b, b).").Success);
        Assert.True(e.Query("retract((g(X, Y) :- Y = X)), \\+ g(b, b).").Success);
        long builds = e.ProgramProductBuilds;
        Assert.True(e.Query("assertz(g(b, 2)).").Success);
        Assert.True(e.Query("g(b, V), V == 2, retract(g(a, _)).").Success);
        Assert.True(e.Query("findall(K-W, g(K, W), L), L == [b-2].").Success);
        Assert.Equal(builds, e.ProgramProductBuilds);
    }
}
