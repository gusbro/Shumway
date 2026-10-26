using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Big integers and rationals across a collection and a backtrack,
/// end to end. A big integer is a block of heap cells (ADR-064), reclaimed
/// by the collector and by the heap top reset; a rational is a side-table
/// entry (ADR-053) that `RationalAlloc` reclaims when backtracking unwinds
/// past the allocation, after a sweep that may have shrunk the table first
/// (a no-op by construction: it truncates only when the table is larger).
/// What matters here is that neither mechanism changes an answer.</summary>
public sealed class NumericSideTableLifetimeTests
{
    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            bigints(0) :- !.
            bigints(N) :- X is 2^70 + N, X > 0, M is N - 1, bigints(M).
            """);
        return e;
    }

    /// <summary>A collection between an allocation and the backtrack that
    /// unwinds past it. Answers must be unaffected.</summary>
    [Fact]
    public void ACollectionBetweenAllocationAndBacktrackingKeepsAnswers()
    {
        var e = Engine();
        Assert.True(e.Query("""
            X is 2^70 + 1,
            ( bigints(500), garbage_collect, fail ; true ),
            Y is X + 1, Z is Y - X, Z =:= 1, X =:= 2^70 + 1.
            """).Success,
            "a big integer held across a collection and a backtrack changed value");
    }

    /// <summary>Big integers created and abandoned inside a failure-driven
    /// loop: the trail already reclaimed these, and the sweep must not make
    /// the arithmetic wrong.</summary>
    [Fact]
    public void AFailureDrivenLoopOverBigIntegersStillComputes()
    {
        var e = Engine();
        Assert.True(e.Query("""
            findall(S, (between(1, 200, I), S is (2^70 + I) - 2^70), L),
            garbage_collect,
            sum_list(L, T), T =:= 20100.
            """).Success,
            "a failure-driven loop over big integers computed the wrong sum");
    }

    /// <summary>Rationals survive the same treatment (ADR-039).</summary>
    [Fact]
    public void RationalsSurviveACollectionAndBacktracking()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            rats(0) :- !.
            rats(N) :- X is (2^70 + N) rdiv 3, X > 0, M is N - 1, rats(M).
            """);
        Assert.True(e.Query("""
            R is 22 rdiv 7,
            ( rats(300), garbage_collect, fail ; true ),
            S is R * 7, S =:= 22.
            """).Success,
            "a rational held across a collection and a backtrack changed value");
    }
}
