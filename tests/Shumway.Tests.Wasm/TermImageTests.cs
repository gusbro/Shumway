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
        id(X, X).
        lookup(K, V) :- t([k(a, 1), k(b, 2), k(c, 3), k(d, 4), k(e, 5), k(f, 6)], K, V).
        t([k(K, V)|_], K, V) :- !.
        t([_|T], K, V) :- t(T, K, V).
        mixed(X) :- id(f([1, 2.5, "str", g(h(i)), [x, y|z]], 'A b', -3, 1000000000000), X).
        text(X) :- id(f("hello world", 'x', 1.5, [1, 2, 3]), X).
        run(0) :- !.
        run(N) :- lookup(c, 3), N1 is N - 1, run(N1).
        """;

    private static readonly string[] Goals =
    {
        "lookup(c, V), V == 3.",
        "lookup(f, V), V == 6.",
        "\\+ lookup(zz, _).",
        "mixed(X), X = f(L, A, N, B), L = [_, F, S, G, T], F =:= 2.5, S == \"str\", G == g(h(i)), T == [x, y|z], A == 'A b', N =:= -3, B =:= 1000000000000.",
        "text(X), X = f(S, A, F, L), S == \"hello world\", A == x, F =:= 1.5, L == [1, 2, 3].",
        "run(100), mixed(X), X = f([_, _, S|_], _, _, _), S == \"str\".",
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
        foreach (string indicator in new[] { "lookup/2", "mixed/1", "text/1" })
            Assert.True(Promoted(tiered, indicator),
                $"{indicator} was not promoted\n" + tiered.IlPromotion.DescribePromotionState());
        foreach (string goal in Goals)
            Assert.True(tiered.Query(goal).Success, goal + " on the tier, promoted");
    }
}
