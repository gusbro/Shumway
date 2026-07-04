using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 I11 — <c>StandardOrderComparator</c> (compare/3, @&lt;/@&gt;/…,
/// sort/2, msort/2, keysort/2, predsort/3) no longer recurses per compound arg
/// / per list element, so it terminates instead of overflowing the host on a
/// long acyclic list OR a cyclic (rational) term — both were uncatchable
/// StackOverflow crashes. The iterative walk must keep the ordinary standard
/// order for finite terms and give the co-inductive answer on cyclic ones.
///
/// <para>A crash here aborts the whole test host, so these tests' real
/// assertion is "the process is still alive and the answer is right".</para>
/// </summary>
public class Phase33I11StandardOrderTests
{
    private static bool Holds(string query) => new PrologEngine().Query(query).Success;

    // ---- ordering correctness (regression) ----

    [Fact]
    public void TypeOrder_VarNumberAtomCompound()
    {
        Assert.True(Holds("X @< 1, 1 @< a, a @< f(0), 1.0 @< 1."));
    }

    [Fact]
    public void Compare3_ReturnsOrderAtom()
    {
        Assert.True(Holds("compare(O, 1, 2), O == (<)."));
        Assert.True(Holds("compare(O, foo, foo), O == (=)."));
        Assert.True(Holds("compare(O, g(2), g(1)), O == (>)."));
    }

    [Fact]
    public void Compound_ByArityThenNameThenArgs()
    {
        Assert.True(Holds("f(1) @< f(1, 0)."));            // lower arity first
        Assert.True(Holds("a(9) @< b(0)."));               // then functor name
        Assert.True(Holds("p(1, a) @< p(1, b)."));         // then args L-to-R
        Assert.True(Holds("p(a, z) @< p(b, a)."));         // leftmost difference wins
    }

    [Fact]
    public void NestedCompound_LeftmostDifferenceWins()
    {
        Assert.True(Holds("f(g(a), z) @< f(g(b), a)."));   // difference inside arg0
        Assert.True(Holds("f(g(a), a) @< f(g(a), b)."));   // arg0 equal, arg1 decides
    }

    [Fact]
    public void Sort_And_Msort()
    {
        Assert.True(Holds("sort([3,1,2,1,3], S), S == [1,2,3]."));      // dedup
        Assert.True(Holds("msort([3,1,2,1,3], S), S == [1,1,2,3,3]."));  // keep dups
    }

    [Fact]
    public void Keysort_ByKeyStable()
    {
        Assert.True(Holds(
            "keysort([b-1, a-2, b-3, a-4], S), S == [a-2, a-4, b-1, b-3]."));
    }

    [Fact]
    public void Sort_ListsOfLists_ByStandardOrder()
    {
        Assert.True(Holds(
            "msort([[1,2,3],[1],[1,2]], S), S == [[1],[1,2],[1,2,3]]."));
    }

    // ---- robustness: long acyclic (was a per-element overflow) ----

    [Fact]
    public void CompareLongLists_Equal_NoOverflow()
    {
        Assert.True(Holds("numlist(1, 40000, L), compare(O, L, L), O == (=)."));
        Assert.True(Holds(
            "numlist(1, 40000, L), numlist(1, 40000, M), compare(O, L, M), O == (=)."));
    }

    [Fact]
    public void CompareLongLists_Unequal_NoOverflow()
    {
        // [1..40000] vs [1..39999]: at the tail M ends ([]) while L continues
        // ([40000|_]); [] @< compound, so M @< L, i.e. L @> M.
        Assert.True(Holds(
            "numlist(1, 40000, L), numlist(1, 39999, M), compare(O, L, M), O == (>)."));
    }

    // ---- robustness: cyclic terms (were infinite recursion) ----

    [Fact]
    public void CompareCyclic_EqualInfiniteTerms()
    {
        Assert.True(Holds("X = f(X), Y = f(Y), compare(O, X, Y), O == (=)."));
    }

    [Fact]
    public void CompareCyclic_DifferByFunctor()
    {
        Assert.True(Holds("X = f(X), Y = g(Y), compare(O, X, Y), O == (<)."));
    }

    [Fact]
    public void CompareCyclic_DifferByLeafArgBeforeCycle()
    {
        Assert.True(Holds("X = s(a, X), Y = s(b, Y), compare(O, X, Y), O == (<)."));
    }

    [Fact]
    public void CompareCyclicList_Terminates()
    {
        Assert.True(Holds("X = [1|X], Y = [1|Y], compare(O, X, Y), O == (=)."));
        Assert.True(Holds("X = [1|X], Y = [2|Y], compare(O, X, Y), O == (<)."));
    }
}
