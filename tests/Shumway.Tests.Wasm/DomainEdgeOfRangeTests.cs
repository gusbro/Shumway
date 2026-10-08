using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A domain at the ends of the integers it holds, and a value past
/// them, on the tier as on Tier-0.
///
/// <para>The forms a wasm module answers $dom_del and $dom_contains with
/// move a bound by one, in 60 bits. They take an Int value and a domain
/// whose bounds are integers, so a side that ends in inf or sup, where one
/// past an end of the range would have to be written, and an integer past
/// the range, both step aside to the builtin. These hold that: an answer
/// from a wrapped bound would be a domain with the wrong values in it and
/// nothing failing.</para></summary>
public sealed class DomainEdgeOfRangeTests
{
    private const string Corpus = """
        kept(D, D2, R) :- ( '$dom_same'(D2, D) -> R = kept ; R = changed ).

        del_least_open(R) :-
            '$dom_new'(inf, 5, D), '$dom_del'(D, -576460752303423488, D2), kept(D, D2, R).
        del_greatest_open(R) :-
            '$dom_new'(0, sup, D), '$dom_del'(D, 576460752303423487, D2), kept(D, D2, R).
        del_greatest_bounded(R) :-
            '$dom_new'(576460752303423486, 576460752303423487, D),
            '$dom_del'(D, 576460752303423487, D2), '$dom_max'(D2, R).
        del_least_bounded(R) :-
            '$dom_new'(-576460752303423488, -576460752303423487, D),
            '$dom_del'(D, -576460752303423488, D2), '$dom_min'(D2, R).
        del_past(R) :-
            '$dom_new'(1, 9, D), X is 2 ^ 70, '$dom_del'(D, X, D2), kept(D, D2, R).
        contains_past_bounded(R) :-
            '$dom_new'(1, 9, D), X is 2 ^ 70,
            ( '$dom_contains'(D, X) -> R = yes ; R = no ).
        contains_past_open(R) :-
            '$dom_new'(1, sup, D), X is 2 ^ 70,
            catch(( '$dom_contains'(D, X) -> R = yes ; R = no ), error(E, _), R = E).
        contains_greatest(R) :-
            '$dom_new'(0, 576460752303423487, D),
            ( '$dom_contains'(D, 576460752303423487) -> R = yes ; R = no ).
        """;

    [Theory]
    [InlineData("del_least_open(kept)")]
    [InlineData("del_greatest_open(kept)")]
    [InlineData("del_greatest_bounded(576460752303423486)")]
    [InlineData("del_least_bounded(-576460752303423487)")]
    [InlineData("del_past(kept)")]
    [InlineData("contains_past_bounded(no)")]
    [InlineData("contains_past_open(representation_error(max_clpfd_integer))")]
    [InlineData("contains_greatest(yes)")]
    public void TheAnswerIsTheEnginesEitherWay(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query($"{goal}.").Success, $"Tier-0: {goal}");
        var (tier, _, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        // Twice: the second call runs the module the first one promoted.
        Assert.True(tier.Query($"{goal}.").Success, $"tier, first call: {goal}");
        Assert.True(tier.Query($"{goal}.").Success, $"tier: {goal}");
    }
}
