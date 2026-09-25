using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary><c>arg/3</c> with a bound index, answered inside the module.
///
/// <para>After the attribute lookup left the ranking it is the heaviest exit
/// clp(Z) makes: 253 of 1,175 builtin requests in one goal, 22%, and 147 of
/// those from a single walker. Indexed, it is a bounds check and one heap
/// read, and the module has both.</para>
///
/// <para>An unbound index is a different predicate: with an SWI-dialect
/// caller it ENUMERATES, which leaves a choice point this form cannot
/// leave. That, and every error shape, steps aside.</para></summary>
public sealed class InlineArgTests(ITestOutputHelper o)
{
    private const string Corpus = """
        a(N, T, A) :- arg(N, T, A).

        first(X)  :- a(1, pair(alpha, beta, gamma), X).
        middle(X) :- a(2, pair(alpha, beta, gamma), X).
        last(X)   :- a(3, pair(alpha, beta, gamma), X).
        % A list is a compound of arity two: head and tail.
        lhead(X)  :- a(1, [x, y, z], X).
        ltail(X)  :- a(2, [x, y, z], X).
        % Out of range FAILS, on both sides and on both shapes.
        past(R)   :- ( a(4, pair(alpha, beta, gamma), _) -> R = yes ; R = no ).
        zero(R)   :- ( a(0, pair(alpha, beta, gamma), _) -> R = yes ; R = no ).
        lpast(R)  :- ( a(3, [x, y], _) -> R = yes ; R = no ).
        % A bound output is COMPARED, not assumed free.
        checks(R)  :- ( a(2, pair(alpha, beta, gamma), beta) -> R = yes ; R = no ).
        rejects(R) :- ( a(2, pair(alpha, beta, gamma), alpha) -> R = yes ; R = no ).
        % An unbound argument in the term comes back unbound and BINDS.
        binds(T, X) :- T = pair(_, _), a(1, T, X), X = bound.
        % The shapes it declines still answer.
        neg(R)   :- catch(a(-1, pair(a, b), _), error(E, _), true),
                    ( var(E) -> R = no ; R = E ).
        nonint(R) :- catch(a(foo, pair(a, b), _), error(E, _), true),
                     ( var(E) -> R = no ; R = E ).
        atomic_term(R) :- catch(a(1, hello, _), error(E, _), true),
                     ( var(E) -> R = no ; R = E ).
        unbound(R) :- catch(a(1, _, _), error(E, _), true),
                      ( var(E) -> R = no ; R = E ).
        """;

    [Theory]
    [InlineData("first(X), X == alpha.")]
    [InlineData("middle(X), X == beta.")]
    [InlineData("last(X), X == gamma.")]
    [InlineData("lhead(X), X == x.")]
    [InlineData("ltail(X), X == [y, z].")]
    [InlineData("past(R), R == no.")]
    [InlineData("zero(R), R == no.")]
    [InlineData("lpast(R), R == no.")]
    [InlineData("checks(R), R == yes.")]
    [InlineData("rejects(R), R == no.")]
    [InlineData("binds(T, X), X == bound, T = pair(bound, _).")]
    [InlineData("neg(R), R = domain_error(not_less_than_zero, -1).")]
    [InlineData("nonint(R), R = type_error(integer, foo).")]
    [InlineData("atomic_term(R), R = type_error(compound, hello).")]
    [InlineData("unbound(R), R == instantiation_error.")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>And it really is inline: an indexed call leaves no builtin
    /// request behind, whether it hits or runs off the end.</summary>
    [DiagTheory]
    [InlineData("middle(X), X == beta.")]
    [InlineData("ltail(X), X == [y, z].")]
    [InlineData("past(R), R == no.")]
    [InlineData("zero(R), R == no.")]
    public void AnIndexedCallDoesNotLeaveTheModule(string goal)
        => Assert.Equal(0L, ExitsOf(goal));

    /// <summary>The counterproof, in red: the shapes that owe an ERROR still
    /// leave. Without it the test above would pass just as well with a form
    /// that answered calls whose answer is a throw.</summary>
    [DiagTheory]
    [InlineData("nonint(R), R = type_error(integer, foo).")]
    [InlineData("atomic_term(R), R = type_error(compound, hello).")]
    public void AnErroringCallStillLeaves(string goal)
        => Assert.True(ExitsOf(goal) > 0, "an error is the engine's");

    private long ExitsOf(string goal)
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(goal).Success);

        long n = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            if (name == "arg" && arity == 3) n = hits;
        o.WriteLine($"exits={n} deopts={WasmTierDelegate.DiagDeopts}");
        return n;
    }
}
