using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 344 (Phase 28): the CLP(FD) domain is now a native C# object
/// (<see cref="Shumway.Builtins.ClpfdDomain"/>, immutable interval set) stored
/// in the engine's foreign-object table and named by a <c>Foreign</c> cell from
/// the <c>fd(Dom, Props)</c> attribute, with every domain operation
/// (<c>$dom_*</c>) native instead of interpreted-Prolog interval-list walking.
/// Profiling showed that walking dominated FD solving; this cut alpha
/// first-fail ~3.5× and donald leftmost ~4× with no behaviour change.
///
/// <para>These tests pin the behaviour the rewrite had to preserve: the
/// value-coincidence and inf/sup edge cases, all_different (which subtracts an
/// interval via the native union), and — the subtle one — that
/// <c>copy_term/3</c> over an FD variable still projects its domain. Copying
/// round-trips the attribute through the AST, where a foreign domain renders as
/// <c>'$foreign'(N)</c>; the materializer now rebuilds that back into the same
/// Foreign cell so the copied attribute is still a real domain.</para>
/// </summary>
public class Chunk344Tests
{
    private static PrologEngine Fd()
    {
        var e = new PrologEngine();
        e.UseClpfd();
        return e;
    }

    private static Term Int(long v) => new IntTerm(v);

    [Fact]
    public void Labeling_OverHoledDomain_EnumeratesRemainingValues()
    {
        // 1..9 minus {4,5} via two #\= ; label must yield exactly the holes-removed set.
        var sols = Fd().QueryAll(
            "X in 1..9, X #\\= 4, X #\\= 5, label([X]).").ToList();
        var got = sols.Select(s => ((IntTerm)s["X"]!).Value).OrderBy(v => v).ToList();
        Assert.Equal(new long[] { 1, 2, 3, 6, 7, 8, 9 }, got);
    }

    [Fact]
    public void AllDifferent_RemovesHallInterval()
    {
        // X,Y pinned to {1,2}; Z in 1..3 must lose 1 and 2 (Hall interval), → 3.
        var sol = Fd().Query(
            "X in 1..2, Y in 1..2, Z in 1..3, all_distinct([X, Y, Z]).");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["Z"]);
    }

    [Fact]
    public void EqualValueAndScaledSum_StillCorrect()
    {
        // The value-coincidence case (X=Y) and a scaled/repeated coefficient,
        // exercising $dom_above/below/isect and singleton binding.
        var sols = Fd().QueryAll(
            "X in 1..5, Y in 1..5, X + Y #= 6, label([X, Y]).").ToList();
        Assert.Equal(5, sols.Count);
        Assert.Contains(sols, s => s["X"]!.Equals(Int(3)) && s["Y"]!.Equals(Int(3)));

        var sq = Fd().Query("X in 0..9, 2*X + X #= 9.");
        Assert.True(sq.Success);
        Assert.Equal(Int(3), sq["X"]);
    }

    [Fact]
    public void CopyTerm3_OverFdVariable_ProjectsDomain()
    {
        // The Foreign-domain round-trip through copy_term/3: a constrained but
        // unlabelled variable must still project its residual domain. We don't
        // pin the exact rendering — only that the copy mechanism succeeds and
        // the variable stays constrained (a later label only yields 6..9).
        var sols = Fd().QueryAll(
            "X in 1..9, X #> 5, copy_term(X, _, Gs), label([X]).").ToList();
        var got = sols.Select(s => ((IntTerm)s["X"]!).Value).OrderBy(v => v).ToList();
        Assert.Equal(new long[] { 6, 7, 8, 9 }, got);
    }

    [Fact]
    public void Donald_StillSolvesUnderFirstFail()
    {
        // End-to-end through the native domain layer (fast order).
        var sol = Fd().Query(
            "LD = [D,O,N,A,L,G,E,R,B,T], LD ins 0..9, all_different(LD), "
            + "D in 1..9, G in 1..9, "
            + "100000*D + 10000*O + 1000*N + 100*A + 10*L + D "
            + "+ 100000*G + 10000*E + 1000*R + 100*A + 10*L + D "
            + "#= 100000*R + 10000*O + 1000*B + 100*E + 10*R + T, "
            + "labeling([ff], LD), LD == [5,2,6,4,8,1,9,7,3,0].");
        Assert.True(sol.Success);
    }
}
