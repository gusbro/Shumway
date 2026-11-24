using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The clauses of a multifile predicate come from several sources, so a file
/// that gathers several of them (an application concatenated from its files)
/// interleaves them with other predicates. The contiguity check, an error by
/// default, does not apply to them; every other predicate keeps it.
/// </summary>
public sealed class MultifileContiguityTests
{
    [Fact]
    public void AMultifilePredicate_MayBeApartInOneFile()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(@"
            :- multifile(p/1).
            p(1).
            q.
            p(2).
            ");

        Assert.True(e.Query("findall(X, p(X), [1, 2]), q.").Success);
    }

    [Fact]
    public void APredicateAnEarlierLoadDeclaredMultifile_MayBeApartInALaterFile()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(":- multifile(p/1).\np(0).\n");
        e.ConsultString(@"
            p(1).
            q.
            p(2).
            ");

        Assert.True(e.Query("findall(X, p(X), [0, 1, 2]).").Success);
    }

    [Fact]
    public void AnyOtherPredicate_ApartInOneFile_IsStillRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new PrologEngine().ConsultString(@"
            p(1).
            q.
            p(2).
            "));
        Assert.Contains("p/1 are not contiguous", ex.Message);
    }

    [Fact]
    public void ADynamicPredicate_ApartInOneFile_IsStillRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new PrologEngine().ConsultString(@"
            :- dynamic(p/1).
            p(1).
            q.
            p(2).
            "));
        Assert.Contains("p/1 are not contiguous", ex.Message);
    }
}
