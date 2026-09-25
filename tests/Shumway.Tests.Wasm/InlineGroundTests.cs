using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary><c>ground/1</c>, answered by the module's own walk.
///
/// <para>73 exits in one clp(Z) goal, and every one a question the module
/// can answer: is there an unbound variable anywhere in this term. An
/// ATTRIBUTED variable is unbound too, which is the case the libraries that
/// call ground/1 hardest make and the easy one to get wrong.</para>
///
/// <para>The engine's walk carries a VISITED set, because a cyclic term with
/// no variables is ground and a walk without one would not terminate. The
/// module has no set and does not need one: its worklist is bounded by the
/// stack limit, so a cycle fills it and the walk declines.</para></summary>
public sealed class InlineGroundTests(ITestOutputHelper o)
{
    private const string Corpus = """
        g(T, R) :- ( ground(T) -> R = yes ; R = no ).

        flat(R)     :- g(hello, R).
        num(R)      :- g(42, R).
        shallow(R)  :- g(f(1, 2), R).
        deep(R)     :- g(f(g(h(1, [2, 3])), k(4)), R).
        % One variable, buried.
        buried(R)   :- g(f(g(h(1, [2, _]))), R).
        % The last argument, which a walk that stops early would miss.
        last(R)     :- g(f(1, 2, 3, _), R).
        % A variable BOUND to a ground term is ground.
        bound(R)    :- X = f(1), g(f(X), R).
        % A bignum and a float are constants.
        bigv(R)     :- B is 2 ^ 200, g(f(B, 1.5), R).
        % An ATTRIBUTED variable is unbound, whatever it carries.
        att(R)      :- put_attr(V, m, hello), g(f(V), R).
        % An empty list and a long one.
        nil(R)      :- g([], R).
        longl(R)    :- numlist(1, 200, L), g(L, R).
        longv(R)    :- numlist(1, 200, L), append(L, [_], M), g(M, R).
        % A partial list: the TAIL is the variable.
        partial(R)  :- g([1, 2 | _], R).
        """;

    [Theory]
    [InlineData("flat(R), R == yes.")]
    [InlineData("num(R), R == yes.")]
    [InlineData("shallow(R), R == yes.")]
    [InlineData("deep(R), R == yes.")]
    [InlineData("buried(R), R == no.")]
    [InlineData("last(R), R == no.")]
    [InlineData("bound(R), R == yes.")]
    [InlineData("bigv(R), R == yes.")]
    [InlineData("att(R), R == no.")]
    [InlineData("nil(R), R == yes.")]
    [InlineData("longl(R), R == yes.")]
    [InlineData("longv(R), R == no.")]
    [InlineData("partial(R), R == no.")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>And it really is the module answering, both ways round: a
    /// ground term and one with a variable in it.</summary>
    [DiagTheory]
    [InlineData("deep(R), R == yes.")]
    [InlineData("buried(R), R == no.")]
    [InlineData("att(R), R == no.")]
    [InlineData("longl(R), R == yes.")]
    [InlineData("partial(R), R == no.")]
    public void TheWalkStaysInTheModule(string goal)
        => Assert.Equal(0L, ExitsOf(goal));

    private long ExitsOf(string goal)
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(goal).Success, "the answer moved");

        long n = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            if (name == "ground" && arity == 1) n = hits;
        o.WriteLine($"{goal} -> ground/1 exits={n} deopts={WasmTierDelegate.DiagDeopts}");
        return n;
    }
}
