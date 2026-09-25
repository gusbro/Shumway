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
        % Composing is the other half: a name and an arity have to become
        % the id a Str cell carries, which is a table of its own.
        composes(T)  :- T =.. [pair, 1, 2].
        composes2(T) :- T =.. [f, g(1), [2], _].
        % Composing a '.'/2 builds a CONS CELL, not a compound named '.':
        % the unifier calls two different tags a mismatch, so a Str would
        % be a term nothing matches. The round trip is what shows it.
        recons(T)    :- T =.. ['.', 1, [2, 3]].
        roundtrip(R) :- [1, 2, 3] =.. L, T =.. L, ( T == [1, 2, 3] -> R = same ; R = T ).
        % A functor NOBODY has interned has no id to find, and interning one
        % is allocation the host owns. The name is BUILT at run time on
        % purpose: written literally, the parser interns it at consult and
        % the case never happens.
        fresh(S, T)  :- atom_concat(never_before, S, A), T =.. [A, 1, 2].
        % The shapes with their own rules: an empty list is a domain error,
        % a one-element list answers with the element, and a partial list is
        % an instantiation error.
        single(T)    :- T =.. [lonely].
        partial(R)   :- catch((_ =.. [f | _]), error(E, _), true),
                        ( var(E) -> R = no ; R = E ).
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
    [InlineData("composes2(T), T = f(g(1), [2], _).")]
    [InlineData("recons(T), T == [1, 2, 3].")]
    [InlineData("roundtrip(R), R == same.")]
    [InlineData("fresh('_seen', T), T = never_before_seen(1, 2).")]
    [InlineData("single(T), T == lonely.")]
    [InlineData("partial(R), R == instantiation_error.")]
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

    /// <summary>Composing is made inside too, when the functor already has
    /// an id.</summary>
    [DiagTheory]
    [InlineData("composes(T), T == pair(1, 2).")]
    [InlineData("composes2(T), T = f(g(1), [2], _).")]
    [InlineData("recons(T), T == [1, 2, 3].")]
    public void ComposingStaysInTheModule(string goal)
        => Assert.Equal(0L, ExitsOf(goal));

    /// <summary>The counterproofs, in red. A functor nobody has interned has
    /// its own rule and answers with the element itself, which the layout
    /// here does not build.</summary>
    [DiagFact]
    public void AOneElementListStaysTheEngines()
        => Assert.True(ExitsOf("single(T), T == lonely.") > 0,
            "a one-element list was composed in the module");

    /// <summary>A functor nobody has interned is a ONE-SHOT: composing it
    /// interns it, so the second call finds it and the warm measurement
    /// every other test here uses would see nothing. Measured on the first
    /// call for that reason, which is also why the name is built at run
    /// time -- written literally, the parser interns it at consult.
    ///
    /// <para>What it guards is the worst answer this code could give: a
    /// module that invented an id would build a term of the wrong NAME, and
    /// no test of shape would catch it.</para></summary>
    [DiagFact]
    public void AFunctorNobodyInternedIsTheEngines()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        // Warmed on a name that DOES exist, so the promotion itself is not
        // what is being measured.
        // Warmed on ANOTHER fresh name, so the predicate is promoted and
        // the measured call is still the first sighting of ITS functor.
        // Warming on the same one would intern it and measure nothing.
        Assert.True(tiered.Query("fresh('_warm', T), functor(T, _, 2).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(
            "fresh('_cold', T), functor(T, N, 2), N == never_before_cold.").Success);

        long n = 0, functors = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
        {
            if (name == "=.." && arity == 2) n = hits;
            if (name == "atom_concat" && arity == 3) functors = hits;
        }
        o.WriteLine($"first call -> =../2 exits={n} atom_concat exits={functors}");
        Assert.True(functors > 0, "the clause never ran on the tier");
        Assert.True(n > 0, "a functor with no id was composed in the module");
    }

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
