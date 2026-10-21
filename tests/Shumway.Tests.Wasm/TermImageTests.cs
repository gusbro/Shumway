using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-063 on the wasm tier: a ground term a body copies from an
/// image is the term the build gave. The module carries the image as a
/// passive data segment and relocates the copy the way the engine does.
/// Every answer is checked against Tier-0.</summary>
public sealed class TermImageTests
{
    private const string Corpus = """
        :- use_module(library(coroutining)).
        id(X, X).
        lookup(K, V) :- t([k(a, 1), k(b, 2), k(c, 3), k(d, 4), k(e, 5), k(f, 6)], K, V).
        t([k(K, V)|_], K, V) :- !.
        t([_|T], K, V) :- t(T, K, V).
        mixed(X) :- id(f([1, 2.5, "str", g(h(i)), [x, y|z]], 'A b', -3, 1000000000000), X).
        text(X) :- id(f("hello world", 'x', 1.5, [1, 2, 3]), X).
        run(0) :- !.
        run(N) :- lookup(c, 3), N1 is N - 1, run(N1).
        pair(X, Y, P) :- id(p(X, [a, b, c], Y, [d, e, f], X), P).
        fresh(P, Q) :- id(f(A, [1, 2, 3], B, g(A, B), _, _), P), id(h(A, B), Q).
        perm(P, Q) :- id(a, A), id(f(A, [x, y, z], B, B), P), id(B, Q).
        attv(P) :- put_attr(V, t, 1), id(f(V, [1, 2, 3], V), P).
        mem(X, [X|_]).
        mem(X, [_|T]) :- mem(X, T).
        back(L) :- findall(P, (mem(X, [1, 2]), id(f(X, [a, b, c], X), P)), L).
        tbl(1, [a, b, c, d, e, f, g, h]).
        tbl(2, [i, j, k, l, m, n, o, p]).
        tree(t(l(1, 2), l(3, 4)), n(l(5, 6), l(7, 8))).
        patt(f(X, g(X, Y), [a, b, c]), Y, h(Y, X)).
        hd(V, f(V, [1, 2, 3], V)).
        frz(V, R) :- freeze(V, R = woke), tbl(2, V).
        """;

    private static readonly string[] Goals =
    {
        "lookup(c, V), V == 3.",
        "lookup(f, V), V == 6.",
        "\\+ lookup(zz, _).",
        "mixed(X), X = f(L, A, N, B), L = [_, F, S, G, T], F =:= 2.5, S == \"str\", G == g(h(i)), T == [x, y|z], A == 'A b', N =:= -3, B =:= 1000000000000.",
        "text(X), X = f(S, A, F, L), S == \"hello world\", A == x, F =:= 1.5, L == [1, 2, 3].",
        "run(100), mixed(X), X = f([_, _, S|_], _, _, _), S == \"str\".",
        "pair(1, 2, P), P == p(1, [a, b, c], 2, [d, e, f], 1).",
        "fresh(P, Q), P = f(A, L, B, G, _, _), L == [1, 2, 3], G == g(A, B), Q == h(A, B), var(A), var(B).",
        "perm(P, Q), P = f(a, [x, y, z], B1, B2), B1 == B2, B1 == Q, var(Q).",
        "attv(P), P = f(W, _, W2), W == W2, get_attr(W, t, 1).",
        "back(L), L == [f(1, [a, b, c], 1), f(2, [a, b, c], 2)].",
        "findall(P, (mem(X, [1, 2, 3]), pair(X, X, P)), Ps), Ps == [p(1, [a, b, c], 1, [d, e, f], 1), p(2, [a, b, c], 2, [d, e, f], 2), p(3, [a, b, c], 3, [d, e, f], 3)].",
        "tbl(2, L), L == [i, j, k, l, m, n, o, p].",
        "tbl(K, [i, j, k, l, m, n, o, p]), K == 2.",
        "tbl(2, [i | T]), T == [j, k, l, m, n, o, p].",
        "findall(L, tbl(_, L), Ls), Ls == [[a, b, c, d, e, f, g, h], [i, j, k, l, m, n, o, p]].",
        "tree(A, B), A == t(l(1, 2), l(3, 4)), B == n(l(5, 6), l(7, 8)).",
        "tree(t(l(1, 2), X), B), X == l(3, 4), B == n(l(5, 6), l(7, 8)).",
        "tree(A, n(l(5, 6), l(7, 8))), A == t(l(1, 2), l(3, 4)).",
        "\\+ tree(t(l(1, 2), l(3, 5)), _).",
        "patt(F, y, H), F = f(X, G, L), G == g(X, y), L == [a, b, c], H == h(y, X).",
        "patt(f(1, g(1, 2), [a, b, c]), Y, H), Y == 2, H == h(2, 1).",
        "hd(7, T), T == f(7, [1, 2, 3], 7).",
        "frz(V, R), R == woke, V == [i, j, k, l, m, n, o, p].",
    };

    // By name, module prefix stripped: a consulted predicate's functor carries its module.
    private static bool Promoted(PrologEngine e, string indicator)
    {
        foreach (int fid in e.IlPromotion.PromotedFunctorIds())
        {
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string name = AtomTable.GetById(atomId)?.Name ?? "";
            if ($"{name[(name.LastIndexOf('$') + 1)..]}/{arity}" == indicator) return true;
        }
        return false;
    }

    [Fact]
    public void TheModuleCopiesTheImagesTheBuildGave()
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);
        var (tiered, members) = TieredEngine.Build(Corpus);
        foreach (string goal in Goals)
        {
            Assert.True(plain.Query(goal).Success, goal + " on Tier-0");
            Assert.True(tiered.Query(goal).Success, goal + " on the tier");
        }
        // ANTI-VACUITY: the predicates with images did promote to the tier.
        foreach (string indicator in new[] { "lookup/2", "mixed/1", "text/1", "pair/3", "fresh/2", "perm/2", "attv/1", "tbl/2", "tree/2", "patt/3", "hd/2" })
            Assert.True(Promoted(tiered, indicator),
                $"{indicator} was not promoted\n" + tiered.IlPromotion.DescribePromotionState());
        foreach (string goal in Goals)
            Assert.True(tiered.Query(goal).Success, goal + " on the tier, promoted");
    }
}
