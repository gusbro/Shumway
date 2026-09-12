using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Before extending a dynamic chain in place, assertz validated
/// every entry's cached byte offsets against the live buffer -- the
/// last-line-of-defense staleness check, run in full on every append. So
/// growing a predicate was quadratic in its own size: 512,000 facts did not
/// finish in five minutes, and the cost was nothing but re-checking entries
/// that had already been checked.
///
/// <para>The check is now the same incremental one reclamation uses: an
/// entry's offsets cannot move while the buffer is the same array object and
/// content never shrinks on it, so a previous verdict stands and only the
/// entries added since need looking at. What the check DEFENDS is untouched
/// -- a chain describing a rebuilt buffer still fails the buffer-identity
/// test and still falls back to the store.</para></summary>
public sealed class AssertChainVerificationTests
{
    /// <summary>COUNTED, not timed: building n facts verifies O(n) entries in
    /// total, not n(n-1)/2 -- which is 1,999,000 at 2,000, so the bound has
    /// two orders of magnitude of air under it. It is a multiple rather than
    /// an equality because a buffer REALLOCATION legitimately re-verifies the
    /// whole chain once (a new array is a new identity), and how many times
    /// the buffer reallocates while growing depends on what capacity the
    /// pool hands the engine -- which varies with the tests that ran before
    /// in the same process (observed 7n to 16n on the x86 lane).</summary>
    [Fact]
    public void GrowingAPredicateVerifiesEachEntryABoundedNumberOfTimes()
    {
        foreach (int n in new[] { 2_000, 8_000 })
        {
            var e = new PrologEngine { Out = new StringWriter() };
            e.ConsultString("""
                :- dynamic(g/1).
                mk(0) :- !.
                mk(N) :- assertz(g(N)), M is N - 1, mk(M).
                """);
            PrologEngine.ChainEntriesVerified = 0;
            e.ConsultString($":- mk({n}).");
            Assert.True(PrologEngine.ChainEntriesVerified < 40L * n,
                $"{PrologEngine.ChainEntriesVerified} entries verified building {n}");
            // ANTI-VACUITY: they all landed, in order, and dispatch sees them.
            Assert.Equal(n, e.QueryAll("g(_).").Count());
            Assert.True(e.Query($"g({n}).").Success);
            Assert.True(e.Query("g(1).").Success);
        }
    }

    /// <summary>What the check exists for, still caught: clauses keep landing
    /// and answering across buffer growth (many reallocations at these
    /// sizes), interleaved with retracts that dirty the chain between
    /// appends.</summary>
    [Fact]
    public void GrowthAcrossReallocationsAndRetractsStaysCoherent()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(h/1).
            churn(0) :- !.
            churn(N) :- assertz(h(N)),
                        ( 0 is N mod 3 -> retract(h(N)) ; true ),
                        M is N - 1, churn(M).
            """);
        e.ConsultString(":- churn(3000).");
        var left = e.QueryAll("h(N).")
            .Select(s => ((Shumway.Compiler.Ast.IntTerm)s["N"]!).Value).ToList();
        Assert.Equal(3000 - 1000, left.Count);
        Assert.True(left.SequenceEqual(left.OrderByDescending(x => x)),
            "clause order broke under growth churn");
        Assert.False(e.Query("h(3).").Success);
        Assert.True(e.Query("h(2).").Success);
    }
}
