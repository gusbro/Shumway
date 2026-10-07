using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A cyclic list is neither a list nor a partial list, and a builtin
/// that walks one in C# without detecting the cycle spins past every safe
/// point: the query cannot even be cancelled. The sort family answers
/// type_error(list, L) for either argument, as SICStus and Scryer do, and
/// the spine primitives terminate. Each query runs against a deadline, so a
/// regression fails here instead of hanging the run.</summary>
public sealed class CyclicListArgumentTests
{
    private static bool Holds(string query)
    {
        var e = new PrologEngine();
        bool? result = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = e.Query(query).Success; }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), $"did not terminate: {query}");
        if (error is not null) throw new Xunit.Sdk.XunitException($"{query} raised {error}");
        return result!.Value;
    }

    [Theory]
    [InlineData("sort(L, _)")]
    [InlineData("sort([b, a], L)")]
    [InlineData("msort(L, _)")]
    [InlineData("msort([b, a], L)")]
    [InlineData("keysort(P, _)")]
    [InlineData("keysort([k-1], P)")]
    [InlineData("sort(0, @<, L, _)")]
    [InlineData("predsort(compare, L, _)")]
    public void TheSortFamilyRefusesACyclicList(string goal)
        => Assert.True(Holds(
            $"L = [a, b|L], P = [k-1|P], catch({goal}, error(type_error(list, C), _), true), "
            + "nonvar(C), '$cyclic_spine'(C)."));

    [Fact]
    public void KeysortReadsAPackedListsElements()
        => Assert.True(Holds("catch(keysort(\"ab\", _), error(type_error(pair, a), _), true)."));

    [Theory]
    // A cycle, entered at once or after a lead-in: Tail is one of its cells.
    [InlineData("L = [a, b, c|L], '$skip_list'(_, L, T), T = [_|_].")]
    [InlineData("C = [p, q|C], L = [s, t|C], '$skip_list'(_, L, T), T = [_|_].")]
    [InlineData("L = [a|L], '$skip_list'(_, L, T), T = [_|_].")]
    // Proper, partial and improper ends, packed lists included.
    [InlineData("'$skip_list'(N, \"abc\", T), N == 3, T == [].")]
    [InlineData("'$skip_list'(N, [a, b|foo], T), N == 2, T == foo.")]
    [InlineData("numlist(1, 100000, L), '$skip_list'(N, L, T), N == 100000, T == [].")]
    // Elements do not count: a cyclic element on a finite spine is a list.
    [InlineData("W = f(W), '$skip_list'(N, [W, W], T), N == 2, T == [].")]
    [InlineData("X = x, '$skip_list'(N, [X, X, X], T), N == 3, T == [].")]
    public void SkipListTerminatesAndNamesTheEnd(string query) => Assert.True(Holds(query));

    [Theory]
    [InlineData("L = [a|L], \\+ is_list(L).")]
    [InlineData("L = [a, b|L], '$cyclic_spine'(L).")]
    [InlineData("\\+ '$cyclic_spine'([a, b, c]).")]
    [InlineData("numlist(1, 100000, L), is_list(L), length(L, 100000).")]
    [InlineData("length(L, 7), is_list(L), \\+ '$cyclic_spine'(L).")]
    [InlineData("L = [a|L], \\+ length(L, 3).")]
    [InlineData("L = [a|L], catch(length(L, _), error(resource_error(_), _), true).")]
    public void TheSpinePrimitivesAgree(string query) => Assert.True(Holds(query));
}
