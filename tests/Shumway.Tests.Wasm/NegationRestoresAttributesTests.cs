using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>What a double negation leaves behind.
///
/// <para><c>\+ \+ Goal</c> proves Goal and then undoes everything it did,
/// attributes included. If a tier leaves them, the variable is still
/// constrained afterwards -- the answers stay right, because the constraint
/// is merely redundant, and the cost lands somewhere that looks unrelated:
/// the TOP LEVEL then has residual goals to project, and projection walks
/// them with clp(Z)'s <c>unwrap_with/3</c>.</para>
///
/// <para>That is where the browser's runaway actually is. Attributed to the
/// tier's builtin requests by caller, 2,178,045 of 2,178,888 calls to
/// <c>=../2</c> come from <c>clpz$unwrap_with/3</c>, which lives inside
/// <c>attribute_goals//1</c> -- the projection, not the search. The
/// propagation itself is identical on both tiers for the first 371
/// attribute writes; the tier then makes 51 more and stops touching
/// attributes at all.</para></summary>
public sealed class NegationRestoresAttributesTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(clpfd)).
        % The constraint lives ONLY inside the negation, so afterwards the
        % variable must be bare again.
        probe(X, R) :-
            \+ \+ (X in 1..9, X #> 5),
            ( '$unattributed_var'(X) -> R = bare ; R = attributed ).
        deeper(B, R) :-
            \+ \+ (Vs = [A,B,C], Vs ins 1..9, all_different(Vs),
                   A #> 5, label(Vs)),
            ( '$unattributed_var'(B) -> R = bare ; R = attributed ).
        % Anti-vacuity: constrained OUTSIDE, so it stays attributed and the
        % probe is shown able to tell the two apart.
        kept(X, R) :-
            X in 1..9,
            ( '$unattributed_var'(X) -> R = bare ; R = attributed ).
        """;

    /// <summary>The variable is constrained OUTSIDE the negation here, so
    /// both tiers must report it attributed: an anti-vacuity check that the
    /// probe can tell the two apart at all.</summary>
    [Fact]
    public void TheProbeSeesAttributesWhenThereAreSome()
    {
        var e = new PrologEngine();
        e.ConsultString(Corpus);
        Assert.True(e.Query("kept(_, R), R == attributed.").Success,
            "the probe cannot see an attribute that is genuinely there");
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("deeper")]
    public void ADoubleNegationLeavesNothingBehind(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        string p0 = Answer(() => plain.Query($"{goal}(_, R), R == bare.").Success);

        var (tiered, _) = TieredEngine.Build(Corpus);
        string t = Answer(() => tiered.Query($"{goal}(_, R), R == bare.").Success);
        o.WriteLine($"{goal}: tier0={p0} tier={t}");
        Assert.Equal(p0, t);
    }

    private static string Answer(System.Func<bool> run)
    {
        try { return run() ? "bare" : "attributed"; }
        catch (System.Exception ex) { return ex.Message; }
    }
}
