using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An assert or retract of a fact, between queries, leaves the
/// compiled program product in place: the persistent buffer already holds
/// the mutation (ADR-015), and the next setup brings only the mutated
/// predicate up to date in the product. Rebuilding the whole product after
/// every such query cost O(program). A rule keeps the full build: its rewrite
/// can mint helpers the next query has to link.</summary>
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
}
