using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>predsort/3 is a merge sort: O(n log n) comparator calls, where
/// the insertion sort it replaced made n(n-1)/2 of them on an ascending
/// list. The rest pins what the comparator sees and what its answers do.</summary>
public class PredsortMergeSortTests
{
    private const string Program = """
        :- dynamic(seen/1).
        counting(O, A, B) :- flag(cmp_calls, C, C + 1), compare(O, A, B).
        by_key(O, _-K1, _-K2) :- compare(O, K1, K2).
        always_foo(foo, _, _).
        unbound_order(_, _, _).
        nondet_cmp(<, _, _).
        nondet_cmp(>, _, _).
        throwing_cmp(_, _, _) :- throw(oops).
        logging(O, A, B) :- assertz(seen(A-B)), compare(O, A, B).
        """;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(Program);
        return e;
    }

    private static bool Holds(string query) => Engine().Query(query).Success;

    [Fact]
    public void AnAscendingListCostsNLogNComparisons_NotNSquared()
    {
        // n = 2048: an insertion sort compares each element with every
        // earlier one (2,096,128 calls); a merge sort stays under n*log2(n).
        Assert.True(Holds(
            "numlist(1, 2048, L), flag(cmp_calls, _, 0), predsort(counting, L, S), "
            + "S == L, flag(cmp_calls, C, C), C =< 22528."));
    }

    [Fact]
    public void AShuffledListCostsNLogNComparisons_AndSortsLikeMsort()
    {
        // (I * 1103) mod 2048 is a permutation of 0..2047 (1103 is odd).
        Assert.True(Holds(
            "findall(X, (between(1, 2048, I), X is (I * 1103) mod 2048), L), "
            + "flag(cmp_calls, _, 0), predsort(counting, L, S), msort(L, M), S == M, "
            + "flag(cmp_calls, C, C), C =< 22528."));
    }

    [Theory]
    [InlineData("predsort(compare, [3, 1, 1, 2], S), S == [1, 2, 3].")]
    [InlineData("predsort(compare, [], S), S == [].")]
    [InlineData("predsort(compare, [x], S), S == [x].")]
    [InlineData("predsort(compare, \"cba\", S), S == [a, b, c].")]
    [InlineData("predsort(compare, [b, a], [a, b]).")]
    [InlineData("\\+ predsort(compare, [b, a], [b, a]).")]
    // Elements the comparator calls = are dropped: the earlier one stays.
    [InlineData("predsort(by_key, [a-1, b-1, c-2, d-2], S), S == [a-1, c-2].")]
    [InlineData("predsort(by_key, [d-2, c-2, b-1, a-1], S), S == [b-1, d-2].")]
    public void SortsAndDropsEqualElements_KeepingTheEarlierOne(string query)
        => Assert.True(Holds(query));

    [Theory]
    [InlineData("catch(predsort(throwing_cmp, [1, 2], _), oops, true).")]
    [InlineData("catch(predsort(throwing_cmp, [1, 2, 3], _), oops, true).")]
    [InlineData("L = [a, b|L], catch(predsort(compare, L, _), error(type_error(list, C), predsort/3), true), C == L.")]
    [InlineData("\\+ predsort(compare, foo, _).")]
    // A comparator that answers neither <, > nor = — or nothing at all —
    // leaves the list as it came, as the insertion sort did.
    [InlineData("predsort(always_foo, [2, 1, 3], S), S == [2, 1, 3].")]
    [InlineData("predsort(unbound_order, [2, 1, 3], S), S == [2, 1, 3].")]
    // A non-deterministic comparator is backtracked into.
    [InlineData("findall(S, predsort(nondet_cmp, [1, 2], S), Ss), Ss == [[2, 1], [1, 2]].")]
    // A partial list enumerates its lengths, as length/2 does.
    [InlineData("findall(S, (predsort(compare, [a|_], S), length(S, N), (N >= 3 -> ! ; true)), Ss), "
        + "Ss = [[a], [V1, a], [V2, V3, a]], var(V1), var(V2), var(V3).")]
    public void ErrorsPropagate_OddAnswersAndPartialListsBehaveAsBefore(string query)
        => Assert.True(Holds(query));

    [Fact]
    public void TheComparatorSeesTheLaterElementFirst()
    {
        // Two elements: one call, Pred(Ord, Later, Earlier).
        Assert.True(Holds(
            "retractall(seen(_)), predsort(logging, [b, a], S), S == [a, b], "
            + "findall(P, seen(P), Ps), Ps == [a-b]."));
    }

    [Fact]
    public void AModuleLocalComparatorIsCalledInItsModule()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(ps_mod, [sorted/1]).
            desc(O, A, B) :- compare(O0, A, B), flip(O0, O).
            flip(<, >). flip(>, <). flip(=, =).
            sorted(S) :- predsort(desc, [2, 3, 1, 3], S).
            """);
        Assert.True(e.Query("ps_mod:sorted(S), S == [3, 2, 1].").Success);
    }
}
