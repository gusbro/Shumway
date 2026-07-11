using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 96 (Phase 7): the common list-library predicates added to the
/// prelude so typical Prolog programs run unchanged — set operations,
/// numeric list folds, the apply family, and the keyed and predicate
/// sorts.
/// </summary>
public class Chunk96Tests
{
    private static PrologEngine Activation() => new();

    private static int Count(string query) => Activation().QueryAll(query).Count();

    private static bool Holds(string query) => Activation().Query(query).Success;

    // ---- select / permutation ----

    [Fact]
    public void Select_RemovesOneOccurrence()
    {
        Assert.True(Holds("select(b, [a,b,c], R), R == [a,c]."));
    }

    [Fact]
    public void Select_EnumeratesEveryOccurrence()
    {
        Assert.Equal(3, Count("select(_, [a,b,c], _)."));
    }

    [Fact]
    public void Permutation_EnumeratesEveryArrangement()
    {
        Assert.Equal(6, Count("permutation([1,2,3], _)."));
    }

    [Fact]
    public void Permutation_ChecksAGivenArrangement()
    {
        Assert.True(Holds("permutation([1,2,3], [3,1,2])."));
        Assert.False(Holds("permutation([1,2,3], [1,2,2])."));
    }

    // ---- membership & set operations ----

    [Fact]
    public void Memberchk_SucceedsAtMostOnce()
    {
        Assert.True(Holds("memberchk(b, [a,b,c])."));
        Assert.Equal(1, Count("memberchk(b, [a,b,b])."));
    }

    [Fact]
    public void Subtract_RemovesTheDeletedElements()
    {
        Assert.True(Holds("subtract([a,b,c,d], [b,d], R), R == [a,c]."));
    }

    [Fact]
    public void Intersection_KeepsTheCommonElements()
    {
        Assert.True(Holds("intersection([a,b,c], [b,c,d], R), R == [b,c]."));
    }

    [Fact]
    public void Union_KeepsLeftOnlyElementsThenAllOfRight()
    {
        Assert.True(Holds("union([a,b,c], [b,c,d], R), R == [a,b,c,d]."));
    }

    [Fact]
    public void Delete_RemovesEveryMatchingElement()
    {
        Assert.True(Holds("delete([a,b,a,c,a], a, R), R == [b,c]."));
    }

    // ---- numeric lists ----

    [Fact]
    public void Numlist_BuildsTheIntegerRange()
    {
        Assert.True(Holds("numlist(1, 5, L), L == [1,2,3,4,5]."));
        Assert.True(Holds("numlist(3, 3, L), L == [3]."));
        Assert.True(Holds("numlist(5, 3, L), L == []."));
    }

    [Fact]
    public void SumList_AddsTheElements()
    {
        Assert.True(Holds("sum_list([1,2,3,4], S), S == 10."));
        Assert.True(Holds("sum_list([], S), S == 0."));
    }

    [Fact]
    public void MaxList_AndMinList_FindTheExtremes()
    {
        Assert.True(Holds("max_list([3,1,4,1,5,9,2,6], M), M == 9."));
        Assert.True(Holds("min_list([3,1,4,1,5], M), M == 1."));
    }

    [Fact]
    public void MaxMember_AndMinMember_UseStandardOrder()
    {
        Assert.True(Holds("max_member(M, [b,a,c]), M == c."));
        Assert.True(Holds("min_member(M, [b,a,c]), M == a."));
    }

    // ---- apply family ----

    [Fact]
    public void Include_KeepsElementsSatisfyingTheGoal()
    {
        Assert.True(Holds("include(integer, [1,a,2,b,3], R), R == [1,2,3]."));
    }

    [Fact]
    public void Exclude_DropsElementsSatisfyingTheGoal()
    {
        Assert.True(Holds("exclude(integer, [1,a,2,b], R), R == [a,b]."));
    }

    [Fact]
    public void Partition_SplitsOnTheGoal()
    {
        Assert.True(Holds(
            "partition(integer, [1,a,2,b], I, E), I == [1,2], E == [a,b]."));
    }

    // ---- pairs ----

    [Fact]
    public void PairsKeysValues_SplitsPairsIntoKeysAndValues()
    {
        Assert.True(Holds(
            "pairs_keys_values([a-1,b-2,c-3], Ks, Vs), Ks == [a,b,c], Vs == [1,2,3]."));
    }

    [Fact]
    public void PairsKeysValues_BuildsPairsFromKeysAndValues()
    {
        Assert.True(Holds("pairs_keys_values(P, [a,b], [1,2]), P == [a-1,b-2]."));
    }

    // ---- sorts ----

    [Fact]
    public void Predsort_SortsAndDropsEqualElements()
    {
        // compare/3 reports = for the duplicate 1, which predsort drops.
        Assert.True(Holds("predsort(compare, [3,1,1,2], S), S == [1,2,3]."));
    }

    [Fact]
    public void Sort4_WholeTermAscendingRemovesDuplicates()
    {
        Assert.True(Holds("sort(0, @<, [3,1,2,1], S), S == [1,2,3]."));
    }

    [Fact]
    public void Sort4_KeepsDuplicatesWithLessEqualOrder()
    {
        Assert.True(Holds("sort(0, @=<, [3,1,2,1], S), S == [1,1,2,3]."));
    }

    [Fact]
    public void Sort4_DescendingOrder()
    {
        Assert.True(Holds("sort(0, @>, [3,1,2,1], S), S == [3,2,1]."));
    }

    [Fact]
    public void Sort4_SortsByAnArgumentKey()
    {
        Assert.True(Holds(
            "sort(1, @>=, [p(1),p(3),p(2)], S), S == [p(3),p(2),p(1)]."));
    }
}
