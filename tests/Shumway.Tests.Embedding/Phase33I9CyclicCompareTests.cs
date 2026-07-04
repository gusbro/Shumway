using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 I9 — <c>==/2</c> and <c>\==/2</c> over a cyclic (rational) term must
/// terminate instead of overflowing the C# stack. The former recursive
/// <c>AreStrStructurallyEqual</c> / <c>AreLisStructurallyEqual</c> descent used
/// one C# frame per node, so comparing two distinct cyclic terms
/// (<c>X=f(X), Y=f(Y), X==Y</c>) recursed forever → uncatchable StackOverflow
/// that crashed the process. The iterative walk with a visited-pair set gives
/// the greatest-fixpoint (co-inductive) reading — the same answer SWI-Prolog
/// gives — and terminates.
///
/// <para>These tests would abort the whole test host (not just fail) under the
/// old code, so their real assertion is "the process is still alive".</para>
/// </summary>
public class Phase33I9CyclicCompareTests
{
    private static bool Holds(string query) => new PrologEngine().Query(query).Success;

    [Fact]
    public void CyclicStr_TwoDistinctButIdentical_AreEqual()
    {
        // X and Y are separately-built cyclic terms f(f(f(...))). Co-inductively
        // equal — and the comparison must terminate rather than overflow.
        Assert.True(Holds("X = f(X), Y = f(Y), X == Y."));
    }

    [Fact]
    public void CyclicStr_DifferentFunctor_AreNotEqual()
    {
        Assert.True(Holds("X = f(X), Y = g(Y), X \\== Y."));
    }

    [Fact]
    public void CyclicStr_SelfCompare_IsEqual()
    {
        Assert.True(Holds("X = f(X), X == X."));
    }

    [Fact]
    public void CyclicStr_DifferentAritySameSpine_AreNotEqual()
    {
        // f(a,X) vs f(b,Y): the co-recursive arg is equal-so-far, but the atom
        // arg differs — must decide false, not spin.
        Assert.True(Holds("X = f(a, X), Y = f(b, Y), X \\== Y."));
    }

    [Fact]
    public void CyclicStr_DeeperUnrolling_StillEqual()
    {
        // X unrolls once per step, Y twice — both denote f(f(f(...))). Equal.
        Assert.True(Holds("X = f(X), Y = f(f(Y)), X == Y."));
    }

    [Fact]
    public void CyclicList_TwoDistinctButIdentical_AreEqual()
    {
        Assert.True(Holds("X = [1|X], Y = [1|Y], X == Y."));
    }

    [Fact]
    public void CyclicList_DifferentHead_AreNotEqual()
    {
        Assert.True(Holds("X = [1|X], Y = [2|Y], X \\== Y."));
    }

    [Fact]
    public void CyclicList_SelfCompare_IsEqual()
    {
        Assert.True(Holds("X = [a,b|X], X == X."));
    }

    // ---- the acyclic cases must keep giving the ordinary answer ----

    [Fact]
    public void AcyclicStr_Equal_And_NotEqual_StillWork()
    {
        Assert.True(Holds("f(a, g(1), [x,y]) == f(a, g(1), [x,y])."));
        Assert.True(Holds("f(a, g(1), [x,y]) \\== f(a, g(1), [x,z])."));
    }

    [Fact]
    public void AcyclicVars_IdentityStillWork()
    {
        Assert.True(Holds("X == X."));
        Assert.True(Holds("X \\== Y."));
    }

    [Fact]
    public void LongAcyclicList_ComparesEqual_NoFalseCycle()
    {
        // numlist builds a fresh 5000-element list; comparing a term to itself
        // must return true (the visited-set must not mistake a long acyclic
        // spine for a cycle).
        Assert.True(Holds("numlist(1, 5000, L), L == L."));
        Assert.True(Holds("numlist(1, 5000, L), numlist(1, 5000, M), L == M."));
        Assert.True(Holds("numlist(1, 5000, L), numlist(1, 4999, M), L \\== M."));
    }

    [Fact]
    public void MixedCyclicAndValueLeaves_Terminate()
    {
        // A cyclic term whose non-recursive arg is a value leaf (float / string
        // / bigint) exercises the value branches inside the iterative walk.
        Assert.True(Holds("X = f(3.14, X), Y = f(3.14, Y), X == Y."));
        Assert.True(Holds("X = f(3.14, X), Y = f(2.71, Y), X \\== Y."));
        Assert.True(Holds("X = f(100000000000000000000, X), " +
                          "Y = f(100000000000000000000, Y), X == Y."));
    }
}
