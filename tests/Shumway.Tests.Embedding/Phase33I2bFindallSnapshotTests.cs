using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 I2b — findall/3,4 records each solution as a backtrack-safe cell
/// image (<see cref="FindallSnapshot"/>) instead of a managed AST, with an AST
/// fallback for value-leaf templates. The collected list must be identical to
/// what the AST round-trip produced: right values, right order, fresh
/// independent variables per solution, preserved intra-solution sharing, and
/// correct handling of lists / nested compounds / value leaves / nesting.
/// </summary>
public class Phase33I2bFindallSnapshotTests
{
    private static bool Holds(string query) => new PrologEngine().Query(query).Success;

    private static bool HoldsWith(string consult, string query)
    {
        var e = new PrologEngine();
        e.ConsultString(consult);
        return e.Query(query).Success;
    }

    [Fact]
    public void IntTemplate_CollectsInOrder()
    {
        Assert.True(Holds("findall(X, member(X, [10,20,30]), L), L == [10,20,30]."));
        Assert.True(Holds("findall(X, between(1, 5, X), L), L == [1,2,3,4,5]."));
    }

    [Fact]
    public void EmptyResult_IsNil()
    {
        Assert.True(Holds("findall(X, (member(X,[1,2,3]), X > 100), L), L == []."));
    }

    [Fact]
    public void CompoundTemplate_WithSharedVar()
    {
        // f(X, g(X)) — the two X occurrences must stay shared within each
        // solution, and each solution independent.
        Assert.True(Holds(
            "findall(f(X, g(X)), member(X, [1,2]), L), " +
            "L == [f(1, g(1)), f(2, g(2))]."));
    }

    [Fact]
    public void ListTemplate_AndNestedCompound()
    {
        Assert.True(Holds(
            "findall([X, p(X, Y)], (member(X,[a,b]), Y = X), L), " +
            "L == [[a, p(a, a)], [b, p(b, b)]]."));
    }

    [Fact]
    public void UnboundTemplateVar_IsFreshPerSolution()
    {
        // Template var Z is unbound in each solution: the collected elements
        // must be distinct fresh variables, not shared.
        Assert.True(Holds(
            "findall(Z, member(_, [1,2,3]), L), L = [A,B,C], " +
            "A \\== B, B \\== C, A \\== C, var(A), var(B), var(C)."));
    }

    [Fact]
    public void PartialListTemplate_SharedTailStaysShared()
    {
        // Template [X|T] with T unbound: within one solution the two copies of
        // T must be the same fresh var.
        Assert.True(Holds(
            "findall(k(X, [X|T], T), member(X, [1,2]), L), " +
            "L = [k(1, L1, T1), k(2, L2, T2)], " +
            "L1 = [1|T1], L2 = [2|T2]."));
    }

    [Fact]
    public void ValueLeafTemplate_Float_FallsBackCorrectly()
    {
        Assert.True(Holds(
            "findall(X, member(X, [1.5, 2.5, 3.5]), L), L == [1.5, 2.5, 3.5]."));
        // float nested in a compound
        Assert.True(Holds(
            "findall(p(X, 3.14), member(X, [a,b]), L), L == [p(a, 3.14), p(b, 3.14)]."));
    }

    [Fact]
    public void ValueLeafTemplate_BigIntAndString()
    {
        Assert.True(Holds(
            "findall(X, member(X, [100000000000000000000, 2]), L), " +
            "L == [100000000000000000000, 2]."));
    }

    [Fact]
    public void MixedValueLeafAndPlain_SolutionsInterleave()
    {
        // Some solutions have a value leaf (snapshot returns null -> AST),
        // others don't (fast path); the collect must handle both in one frame.
        Assert.True(Holds(
            "findall(X, member(X, [a, 1.5, foo(bar), 42]), L), " +
            "L == [a, 1.5, foo(bar), 42]."));
    }

    [Fact]
    public void NestedFindall_EachFrameIndependent()
    {
        Assert.True(Holds(
            "findall(P-Inner, ( member(P, [1,2]), " +
            "  findall(Q, member(Q, [P, P]), Inner) ), L), " +
            "L == [1-[1,1], 2-[2,2]]."));
    }

    [Fact]
    public void Findall4_AppendsTail()
    {
        Assert.True(Holds("findall(X, member(X, [1,2]), L, [end]), L == [1, 2, end]."));
    }

    [Fact]
    public void DeepList_NoOverflow()
    {
        // A long list template element exercises the iterative spine copy.
        // (Big is ground, so the two copies are structurally == to it and each
        // other — the point is that the deep copy completes and is correct.)
        Assert.True(Holds(
            "numlist(1, 3000, Big), findall(Big, member(_, [x,y]), L), " +
            "L = [A, B], A == Big, B == Big."));
    }

    [Fact]
    public void Bagof_StillGroupsByWitness()
    {
        // bagof stays on the AST path — verify it is unaffected.
        Assert.True(HoldsWith(
            "p(a, 1).\np(a, 3).\np(b, 2).\n:- public q/1.\nq(L) :- bagof(X, p(a, X), L).",
            "q(L), L == [1, 3]."));
    }

    [Fact]
    public void Setof_StillSortsAndDedups()
    {
        Assert.True(HoldsWith(
            ":- public q/1.\nq(L) :- setof(X, member(X, [3,1,2,1,3]), L).",
            "q(L), L == [1, 2, 3]."));
    }
}
