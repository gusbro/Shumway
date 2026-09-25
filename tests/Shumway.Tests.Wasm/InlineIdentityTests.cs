using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary><c>==/2</c> and <c>\==/2</c> between two COMPOUNDS, answered
/// inside the module.
///
/// <para>The inline compare could already settle a pair with a simple side.
/// Two compounds it could not, and those were the heaviest exit left in
/// clp(Z): 241 of 922 builtin requests in one goal, 26%. The module now
/// carries a comparator beside its unifier, walking both terms over a
/// worklist above the stack top.</para>
///
/// <para>It answers with LESS than unification needs: two distinct cells
/// that are variables are two distinct terms, so where the unifier binds,
/// this decides. Bignums, rationals, packed strings and foreign terms step
/// aside, because equal values there can wear different cells.</para>
/// </summary>
public sealed class InlineIdentityTests(ITestOutputHelper o)
{
    private const string Corpus = """
        eq(A, B, R)  :- ( A == B -> R = yes ; R = no ).
        neq(A, B, R) :- ( A \== B -> R = yes ; R = no ).

        % Deep, and equal only at the last leaf: a comparator that stopped at
        % the functor would pass the first of these and fail the second.
        deep(R)     :- eq(f(g(h(1, a), [x, y]), z), f(g(h(1, a), [x, y]), z), R).
        deep_ne(R)  :- eq(f(g(h(1, a), [x, y]), z), f(g(h(1, a), [x, w]), z), R).
        % Same name, different arity, and same arity different name.
        arity(R)    :- eq(f(a, b), f(a, b, c), R).
        name(R)     :- eq(f(a, b), g(a, b), R).
        % A list against a compound of the same shape, and lists of lists.
        lists(R)    :- eq([1, [2, 3], 4], [1, [2, 3], 4], R).
        lists_ne(R) :- eq([1, [2, 3], 4], [1, [2, 9], 4], R).
        % Variables: a term is identical to itself, and two fresh variables
        % are two different terms even though they UNIFY.
        same_var(R) :- X = p(V, V), eq(X, X, R).
        two_vars(R) :- eq(p(_), p(_), R).
        % A shared variable makes two separately built terms identical.
        shared(R)   :- eq(p(V, 1), p(V, 1), R).
        % Bound to the same value, through different paths.
        bound(R)    :- V = 7, W = 7, eq(p(V), p(W), R).
        % Floats and negative zero, which the engine canonicalises.
        floats(R)   :- eq(p(1.5), p(1.5), R).
        zeros(R)    :- eq(p(-0.0), p(0.0), R).
        % The negated form has to agree on every one of these.
        deep_n(R)    :- neq(f(g(h(1, a), [x, y]), z), f(g(h(1, a), [x, y]), z), R).
        deep_ne_n(R) :- neq(f(g(h(1, a), [x, y]), z), f(g(h(1, a), [x, w]), z), R).
        two_vars_n(R) :- neq(p(_), p(_), R).
        % The shapes it declines still answer.
        bigs(R)     :- X is 2 ^ 200, Y is 2 ^ 200, eq(p(X), p(Y), R).
        strings(R)  :- atom_chars(hello, C), eq(p(C), p("hello"), R).
        """;

    [Theory]
    [InlineData("deep(R), R == yes.")]
    [InlineData("deep_ne(R), R == no.")]
    [InlineData("arity(R), R == no.")]
    [InlineData("name(R), R == no.")]
    [InlineData("lists(R), R == yes.")]
    [InlineData("lists_ne(R), R == no.")]
    [InlineData("same_var(R), R == yes.")]
    [InlineData("two_vars(R), R == no.")]
    [InlineData("shared(R), R == yes.")]
    [InlineData("bound(R), R == yes.")]
    [InlineData("floats(R), R == yes.")]
    [InlineData("zeros(R), R == yes.")]
    [InlineData("deep_n(R), R == no.")]
    [InlineData("deep_ne_n(R), R == yes.")]
    [InlineData("two_vars_n(R), R == yes.")]
    [InlineData("bigs(R), R == yes.")]
    [InlineData("strings(R), R == yes.")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>And it really is inline: a compound comparison leaves no
    /// builtin request behind, whichever way it comes out.</summary>
    [DiagTheory]
    [InlineData("deep(R), R == yes.")]
    [InlineData("deep_ne(R), R == no.")]
    [InlineData("lists(R), R == yes.")]
    [InlineData("two_vars(R), R == no.")]
    [InlineData("deep_ne_n(R), R == yes.")]
    public void AComparisonOfCompoundsDoesNotLeaveTheModule(string goal)
        => Assert.Equal(0L, ExitsOf(goal));

    /// <summary>The counterproof, in red: a bignum inside still leaves. Two
    /// equal bignums can wear different cells, so cell identity is not term
    /// identity and the walk must decline rather than answer.</summary>
    [DiagFact]
    public void ABignumInsideStillLeaves()
        => Assert.True(ExitsOf("bigs(R), R == yes.") > 0,
            "equal bignums can wear different cells");

    private long ExitsOf(string goal)
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(goal).Success);

        long n = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            if ((name == "==" || name == "\\==") && arity == 2) n += hits;
        o.WriteLine($"exits={n} deopts={WasmTierDelegate.DiagDeopts}");
        return n;
    }
}
