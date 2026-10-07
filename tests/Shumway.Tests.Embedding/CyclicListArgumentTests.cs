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

    private const string Cycles =
        "L = [a, b|L], C = [0'a, 0'b|C], Ch = [a, b|Ch], Q = [quoted(true)|Q], "
        + "R = [variables(_)|R], F = [force(true)|F], W = [type(text)|W]";

    [Theory]
    // Text read from a list, and option lists: what SICStus and Scryer answer.
    [InlineData("atom_codes(_, C)")]
    [InlineData("atom_chars(_, Ch)")]
    [InlineData("string_codes(_, C)")]
    [InlineData("string_chars(_, Ch)")]
    [InlineData("format(C, [])")]
    [InlineData("format('~w~w', L)")]
    [InlineData("format('~s', [C])")]
    [InlineData("write_term(t, Q)")]
    [InlineData("read_term(user_input, _, R)")]
    [InlineData("close(user_output, F)")]
    public void TextAndOptionBuiltinsRefuseACyclicList(string goal)
        => Assert.True(Holds(
            $"{Cycles}, catch({goal}, error(type_error(list, X), _), true), "
            + "nonvar(X), '$cyclic_spine'(X)."), goal);

    [Fact]
    public void OpenRefusesACyclicOptionList()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shumway-cyclic-{Guid.NewGuid():N}.txt")
            .Replace('\\', '/');
        try
        {
            Assert.True(Holds(
                $"{Cycles}, catch(open('{path}', write, _, W), error(type_error(list, X), _), true), "
                + "nonvar(X), '$cyclic_spine'(X)."));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ACyclicVariableNamesListIsAMalformedOption()
        => Assert.True(Holds(
            "V = ['X' = _|V], catch(write_term(t, [variable_names(V)]), "
            + "error(domain_error(write_option, variable_names(_)), _), true)."));

    [Fact]
    public void ACyclicCodeListIsNotACodeList()
        => Assert.True(Holds("C = [0'a, 0'b|C], \\+ '$is_code_list'(C, _)."));

    [Theory]
    // string_chars/string_codes walked cons cells only: a packed list (the
    // default reading of "ab") was "not a proper list".
    [InlineData("string_chars(S, \"ab\"), S == [a, b]")]
    [InlineData("atom_codes(ab, Cs), string_codes(S, Cs), S == [a, b]")]
    public void StringBuiltinsReadAPackedList(string query) => Assert.True(Holds(query + "."));

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
