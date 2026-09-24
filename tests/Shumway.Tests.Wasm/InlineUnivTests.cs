using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary><c>=../2</c> with the term BOUND, built inside the module.
///
/// <para>All 81 of clp(Z)'s remaining univ calls come from one walker, and
/// the shape is the same every time: a term in hand, a list wanted. The
/// layout is the builtin's, cell for cell.</para>
///
/// <para>Arguments are copied verbatim, which is what makes an UNBOUND one
/// come out right: a variable's cell is a reference to where it lives, so
/// the copy refers to the same variable rather than making a new one. The
/// test that pins this binds through the list and reads the term.</para>
/// </summary>
public sealed class InlineUnivTests(ITestOutputHelper o)
{
    private const string Corpus = """
        u(T, L) :- T =.. L.

        % compound/1 is a type test of the engine, so this is named
        % apart: a corpus clause that collides with a builtin is
        % ignored with a warning and every answer below it moves.
        three(L)     :- u(pair(a, b, c), L).
        unary(L)     :- u(f(x), L).
        atomv(L)     :- u(hello, L).
        intv(L)      :- u(42, L).
        floatv(L)    :- u(1.5, L).
        cons(L)      :- u([x, y], L).
        % An argument that is a VARIABLE has to come out as that variable,
        % not as a copy: binding through the list must bind the term.
        shares(T, X) :- T = f(_, b), u(T, [_, A, _]), A = bound, T = f(X, _).
        % Nested terms are moved, not descended into.
        nested(L)    :- u(f(g(1), [2]), L).
        % Composing is a different predicate, and it still answers.
        composes(T)  :- T =.. [pair, 1, 2].
        % A bignum rides as a cell nobody reads.
        bigv(L)      :- B is 2 ^ 200, u(B, L).
        """;

    [Theory]
    [InlineData("three(L), L == [pair, a, b, c].")]
    [InlineData("unary(L), L == [f, x].")]
    [InlineData("atomv(L), L == [hello].")]
    [InlineData("intv(L), L == [42].")]
    [InlineData("floatv(L), L == [1.5].")]
    [InlineData("cons(L), L = ['.', x, [y]].")]
    [InlineData("shares(T, X), X == bound.")]
    [InlineData("nested(L), L = [f, g(1), [2]].")]
    [InlineData("composes(T), T == pair(1, 2).")]
    [InlineData("bigv(L), L = [B], B =:= 2 ^ 200.")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>A decomposing call leaves no builtin request behind, for
    /// every shape the layout covers.</summary>
    [DiagTheory]
    [InlineData("three(L), L == [pair, a, b, c].")]
    [InlineData("atomv(L), L == [hello].")]
    [InlineData("cons(L), L = ['.', x, [y]].")]
    [InlineData("bigv(L), L = [B], B =:= 2 ^ 200.")]
    public void ADecomposingCallDoesNotLeaveTheModule(string goal)
        => Assert.Equal(0L, ExitsOf(goal));

    /// <summary>The counterproof, in red: COMPOSING reads the list instead
    /// of writing it, and stays the engine's.</summary>
    [DiagFact]
    public void ComposingStillLeaves()
        => Assert.True(ExitsOf("composes(T), T == pair(1, 2).") > 0,
            "composing is a different predicate");

    private long ExitsOf(string goal)
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(goal).Success, "the answer moved");

        long n = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            if (name == "=.." && arity == 2) n = hits;
        o.WriteLine($"{goal} -> =../2 exits={n} deopts={WasmTierDelegate.DiagDeopts}");
        return n;
    }
}
