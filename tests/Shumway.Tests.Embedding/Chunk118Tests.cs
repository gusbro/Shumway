using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 118 (Phase 8, ADR-015 chunk C): live dynamic-predicate dispatch.
/// A direct call to a dynamic predicate now sees a change made earlier in
/// the same query — the ISO logical update view. A predicate modified
/// mid-query is recompiled lazily and the call redirected to it.
/// </summary>
public class Chunk118Tests
{
    private static PrologEngine Dyn()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        return e;
    }

    [Fact]
    public void AssertThenDirectCall_SameQuery()
        => Assert.True(Dyn().Query("assertz(d(1)), d(1).").Success);

    [Fact]
    public void AssertThenDirectCall_BoundResult()
    {
        var sols = Dyn().QueryAll("assertz(d(7)), d(X).").ToList();
        Assert.Single(sols);
        Assert.Equal("7", sols[0]["X"]!.ToString());
    }

    [Fact]
    public void AssertSeveralThenEnumerate_SameQuery()
    {
        var e = Dyn();
        Assert.Equal(3, e.QueryAll(
            "assertz(d(1)), assertz(d(2)), assertz(d(3)), d(_).").Count());
    }

    [Fact]
    public void RetractThenDirectCall_SameQuery()
    {
        var e = Dyn();
        e.Query("assertz(d(1)), assertz(d(2)).");
        // retract d(1) then confirm only d(2) is callable, same query.
        Assert.True(e.Query("retract(d(1)), \\+ d(1), d(2).").Success);
    }

    [Fact]
    public void DirectCallBeforeAndAfterAssert()
    {
        var e = Dyn();
        // d(1) fails (empty), then asserted, then succeeds — one query.
        Assert.True(e.Query("( d(1) -> fail ; true ), assertz(d(1)), d(1).").Success);
    }

    [Fact]
    public void DynamicPredicateWithRuleBody_CutWorks()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic pick/1.");
        // assert a rule whose body has a cut; the ! must commit.
        e.Query("assertz((pick(X) :- member(X, [a,b,c]), !)).");
        var sols = e.QueryAll("pick(X).").ToList();
        Assert.Single(sols);
        Assert.Equal("a", sols[0]["X"]!.ToString());
    }

    [Fact]
    public void AssertThenFindall_SameQuery()
    {
        var sols = Dyn().QueryAll(
            "assertz(d(1)), assertz(d(2)), findall(X, d(X), L), length(L, N).")
            .ToList();
        Assert.Single(sols);
        Assert.Equal("2", sols[0]["N"]!.ToString());
    }

    [Fact]
    public void MidQueryComputedFloatLiteral_IsVisible()
    {
        // The asserted float is computed at runtime — not a literal in the
        // query text — so recompiling the predicate interns a brand-new
        // pool entry the interpreter must still resolve.
        var e = Dyn();
        Assert.True(e.Query(
            "X is 1.0 / 3.0, assertz(d(X)), d(Y), Y =:= X.").Success);
    }

    [Fact]
    public void CounterIdiom_RetractAssertInLoop()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic ctr/1.\nctr(0).");
        Assert.True(e.Query(
            "between(1, 50, _), retract(ctr(N)), N1 is N + 1, assertz(ctr(N1)), " +
            "fail ; true.").Success);
        Assert.True(e.Query("ctr(50).").Success);
    }

    [Fact]
    public void Between_FollowedByThreeArgBuiltin_EnumeratesFully()
    {
        // Regression: between/3's choice point must save/restore its three
        // argument registers (arity 3). A following body goal whose builtin
        // call takes >= 3 args (here plus/3) clobbers between's result
        // register X2; if the CP doesn't restore it, the enumeration breaks
        // after one or two values. Was latent until arithmetic work surfaced
        // it (the inlined arith builtin is 4-arg). plus/N is unrelated — any
        // 3-arg builtin between the generator and the fail reproduces it.
        var e = new PrologEngine();
        // Five values, 11..15 — not a truncated prefix.
        Assert.True(e.Query(
            "findall(X, (between(1, 5, N), plus(N, 10, X)), L), L == [11,12,13,14,15].")
            .Success);
    }
}
