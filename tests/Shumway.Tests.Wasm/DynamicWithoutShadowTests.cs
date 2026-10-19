using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-054, the mirror of the IL store's settled answer: a dynamic
/// predicate with no snapshot this query (no clauses, or mutated since the
/// query's setup) is asked once past the threshold; the next query, whose
/// setup sees its clauses, promotes it as a shadow.</summary>
public sealed class DynamicWithoutShadowTests
{
    private const string Corpus = """
        :- dynamic(pushback/1).
        scan(0) :- !.
        scan(N) :- ( pushback(_) -> true ; true ), N1 is N - 1, scan(N1).
        """;

    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void ADynamicMutatedInTheQueryPromotesAsAShadowInTheNext()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        int fid = Fid("pushback", 1);
        // The assertz makes it stale for the rest of this query.
        Assert.True(tiered.Query("assertz(pushback(a)), scan(5000).").Success);
        Assert.False(tiered.IlPromotion.Wasm!.HasShadow(fid));
        // The next query's setup sees the clause: the cached answer was the
        // query's, not the store's.
        Assert.True(tiered.Query("scan(5000).").Success);
        Assert.True(tiered.IlPromotion.Wasm!.HasShadow(fid), "pushback/1 was not promoted as a snapshot");
        var r = tiered.Query("scan(10), pushback(X).");
        Assert.True(r.Success);
        Assert.Equal("a", r.Bindings["X"].ToString());
    }
}
