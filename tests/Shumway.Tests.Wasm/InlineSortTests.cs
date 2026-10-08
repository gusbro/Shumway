using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>sort/2 answered inside the module over its own standard-order
/// comparator: variables by address, integers by value, compounds by arity,
/// functor and arguments; the sort clp(Z) makes of a queue list on every
/// constraint it posts (2,760 requests on queens24). A pair only the host
/// orders, two different atoms, a float, a packed string, sends the whole
/// call out, as do a spine that is no proper list and a second argument
/// that is no partial list; the host owns those errors.</summary>
public sealed class InlineSortTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(lists)).
        loop(0, _) :- !.
        loop(N, L) :- sort(L, S), S = [_|_], N1 is N - 1, loop(N1, L).
        ints(N) :- loop(N, [3, 1, 2, 3, -5, 1, 7]).
        vars(N) :- loop(N, [C, A, B, A, C]).
        args(N) :- loop(N, [f(B, 1), f(A, 2), f(A, 1), f(B, 1)]).
        queues(N) :-
            put_attr(A, m, 1), put_attr(B, m, 2),
            loop(N, [queue(A, x, []), queue(B, y, []), queue(A, x, [])]).
        lists(N) :- loop(N, [[2, 1], [1, 2], [1], [1, 2]]).
        long(N) :- numlist(1, 300, L0), reverse(L0, L), loop(N, L).
        atoms(N) :- loop(N, [b, a, c, a]).
        floats(N) :- loop(N, [1.5, 1, 2.5, 2]).
        packed(N) :- atom_chars(abc, Cs), loop(N, [Cs, [x]]).
        """;

    [Fact]
    public void TheAnswersAreTheInterpreters()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, _) = TieredEngine.Build(Corpus);
        foreach (string goal in new[]
        {
            "sort([3, 1, 2, 3, -5, 1, 7], S), S == [-5, 1, 2, 3, 7].",
            "sort([C, A, B, A, C], S), length(S, 3), msort([C, A, B], S).",
            "sort([f(B, 1), f(A, 2), f(A, 1), f(B, 1)], S), length(S, 3), msort([f(A, 1), f(A, 2), f(B, 1)], S).",
            "put_attr(A, m, 1), put_attr(B, m, 2), sort([queue(A, x, []), queue(B, y, []), queue(A, x, [])], S), length(S, 2).",
            "sort([[2, 1], [1, 2], [1], [1, 2]], S), S == [[1], [1, 2], [2, 1]].",
            "numlist(1, 300, L0), reverse(L0, L), sort(L, S), S == L0.",
            "sort([b, a, c, a], S), S == [a, b, c].",
            "sort([1.5, 1, 2.5, 2], S), S == [1.5, 2.5, 1, 2].",     // ISO 7.2.1: a float precedes an integer, whatever the values
            "sort([1, 1.0], S), S == [1.0, 1].",
            "atom_chars(abc, Cs), sort([Cs, [x]], S), S == [[a, b, c], [x]].",
            "sort([], S), S == [].",
            "sort([a], S), S == [a].",
            "sort([b, a], [X|T]), X == a, T == [b].",
            "\\+ sort([a, b], [b, a]).",
            "sort([f(X), g(Y), f(Y)], S), length(S, 3), S = [f(_), f(_), g(_)].",
            "sort([g(a, b), f(a, b, c), h(a)], S), S == [h(a), g(a, b), f(a, b, c)].",
            "catch(sort(foo, _), error(type_error(list, foo), _), true).",
            "catch(sort([a|_], _), error(instantiation_error, _), true).",
            "catch(sort([a], foo), error(type_error(list, foo), _), true).",
            "ints(20).", "vars(20).", "args(20).", "queues(20).", "lists(20).", "long(3).",
            "atoms(20).", "floats(20).", "packed(20).",
        })
        {
            Assert.True(plain.Query(goal).Success, goal);
            Assert.True(tiered.Query(goal).Success, goal + " under the module");
        }
    }

    private static long SortExits()
    {
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            if (n == "sort" && a == 2) return hits;
        return 0;
    }

    /// <summary>The counters: integers, variables, same-functor compounds,
    /// attributed queue terms, lists of lists and a long list all sort in
    /// the module; two different atoms, a float and a packed string leave
    /// (the anti-vacuity: the site still exits when it must).</summary>
    [DiagFact]
    public void TheSortStaysInTheModule()
    {
        var (engine, _) = TieredEngine.Build(Corpus);
        foreach (string goal in new[] { "ints(3).", "vars(3).", "args(3).", "queues(3).", "lists(3).",
                                        "long(3).", "atoms(3).", "floats(3).", "packed(3)." })
            Assert.True(engine.Query(goal).Success, goal);

        void Check(string goal, string what, bool leaves)
        {
            WasmTierDelegate.ResetDiag();
            Assert.True(engine.Query(goal).Success, goal);
            o.WriteLine($"{what}: sort exits={SortExits()} deopts={WasmTierDelegate.DiagDeopts}");
            if (leaves)
                Assert.True(SortExits() >= 100, $"{what}: {SortExits()} exits, the host was not asked");
            else
                Assert.True(SortExits() == 0, $"{what}: {SortExits()} exits, the module did not sort");
            Assert.Equal(0, WasmTierDelegate.DiagDeopts);
        }
        Check("ints(300).", "integers", leaves: false);
        Check("vars(300).", "variables", leaves: false);
        Check("args(300).", "compounds of one functor", leaves: false);
        Check("queues(300).", "queue terms over attributed variables", leaves: false);
        Check("lists(300).", "lists of lists", leaves: false);
        Check("long(100).", "a list of 300", leaves: false);
        Check("atoms(300).", "two different atoms", leaves: true);
        Check("floats(300).", "a float", leaves: true);
        Check("packed(300).", "a packed string", leaves: true);
    }
}
