using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

public sealed partial class IndexedDynamicChurnTests
{
    private static int FunctorOf(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    // The in-place paths of the indexed layout read a fact's first argument
    // off its image (ADR-063). Facts keyed by an atom, an integer, a
    // structure, a list and a variable enter the layout by assertz and
    // asserta, under a key it has and under one it has not, and leave it by
    // retract: their terms stay unbuilt. The four facts the layout was
    // compiled from have theirs, as the compiler reads them.
    [Fact]
    public void FactsEnteringAndLeavingTheIndexedLayoutStayImages()
    {
        var hot = SameAnswersAsAPlainChain(
            "assertz(p(a, 1)), assertz(p(1, 2)), assertz(p(f(x), 3)), assertz(p([h], 4))",
            Warm,
            "assertz(p(a, 9)), assertz(p([k], 8)), assertz(p(X, X))",
            "assertz(p(b, 5)), assertz(p(2, 6)), assertz(p(g(y), 7))",
            "asserta(p(c, 10)), asserta(p(a, 11)), asserta(p(3, 12))",
            "retract(p(b, 5)), retract(p(2, _)), retract(p(g(y), _)), retract(p([k], _)), retract(p(c, 10)), retract(p(a, 1))");
        Assert.True(hot.IndexedRetracts >= 6, "the retracts did not take the indexed path");
        var clauses = hot.DynamicClausesFor(FunctorOf("p", 2));
        Assert.Equal(7, clauses.Count);
        int withTerms = 0;
        foreach (var c in clauses)
        {
            Assert.NotNull(c.HeadImage);
            if (c.HasTerm) withTerms++;
        }
        Assert.Equal(3, withTerms);
    }
}
