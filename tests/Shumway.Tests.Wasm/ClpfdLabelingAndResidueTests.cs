using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>library(clpfd)'s value stepping and its residual goals, on the
/// wasm tier as on Tier-0. The stepping ($dom_next, $dom_prev, $dom_nth0)
/// and the corner product (clpfd_bxmul) are builtins a module exits to; the
/// orders and the residue are the library's, and must not depend on which
/// tier ran the propagators.</summary>
public sealed class ClpfdLabelingAndResidueTests
{
    private const string Corpus = """
        :- use_module(library(clpfd)).
        wide_up(X) :- X in 0..3000000000, label([X]).
        wide_down(X) :- X in 0..3000000000, labeling([down], [X]).
        wide_middle(X) :- X in 0..3000000000, labeling([middle], [X]).
        holes_middle(L) :- X in 1..9, X #\= 4, X #\= 5, findall(X, labeling([middle], [X]), L).
        holes_down(L) :- X in 1..9, X #\= 5, findall(X, labeling([down], [X]), L).
        random_all(S) :- X in 1..12, X #\= 7, findall(X, labeling([random_value], [X]), L), msort(L, S).
        reif(G) :- B #<==> (X #> 5), copy_term([B, X], _, G).
        square(D) :- X in -5..5, Y #= X*X, copy_term(Y, Y1, [Y1 in D|_]).
        product(D) :- X in 1..10, Z #> 0, Y #= X*Z, copy_term(Y, Y1, [Y1 in D|_]).
        """;

    [Theory]
    [InlineData("wide_up(X), X == 0")]
    [InlineData("wide_down(X), X == 3000000000")]
    [InlineData("wide_middle(X), X == 1499999999")]
    [InlineData("holes_middle(L), L == [3,6,2,7,1,8,9]")]
    [InlineData("holes_down(L), L == [9,8,7,6,4,3,2,1]")]
    [InlineData("random_all(S), S == [1,2,3,4,5,6,8,9,10,11,12]")]
    [InlineData("reif(G), G = [_ in 0..1, (_ #> 5 #<==> _) | _]")]
    [InlineData("square(D), D == 0..25")]
    [InlineData("product(D), D == 1..sup")]
    public void TheAnswerIsTheEnginesEitherWay(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query($"{goal}.").Success, $"Tier-0: {goal}");
        var (tier, _, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        // Twice: the second call runs the modules the first one promoted.
        Assert.True(tier.Query($"{goal}.").Success, $"tier, first call: {goal}");
        Assert.True(tier.Query($"{goal}.").Success, $"tier: {goal}");
    }
}
