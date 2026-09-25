using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary><c>functor/3</c> answered inside the module.
///
/// <para>The heaviest exit clp(Z) makes: 867 of 2,846 builtin requests in
/// one goal, 30%. The module already holds what the answer needs -- the
/// term is in a register and the functor mirror packs (atom id, arity) in
/// one word -- so the decomposing mode never has to leave.</para>
///
/// <para>Decomposing only. Constructing allocates and raises on shapes this
/// has no business knowing, so an unbound first argument steps aside, and
/// so does a list cell, whose functor is not in the mirror.</para></summary>
public sealed class InlineFunctorTests(ITestOutputHelper o)
{
    private const string Corpus = """
        f(T, N, A) :- functor(T, N, A).
        % Both answers checked, and against a term whose name and arity are
        % not guessable from the shape of the call.
        compound(N, A) :- f(pair(a, b, c), N, A).
        atomic_name(N, A) :- f(hello, N, A).
        number_name(N, A) :- f(42, N, A).
        % A bound output must be COMPARED, not assumed free.
        checks(R) :- ( f(pair(a, b, c), pair, 3) -> R = yes ; R = no ).
        rejects(R) :- ( f(pair(a, b, c), pair, 2) -> R = yes ; R = no ).
        rejects_name(R) :- ( f(pair(a, b, c), other, 3) -> R = yes ; R = no ).
        % The shapes it declines still answer.
        alist(N, A) :- f([x, y], N, A).
        built(T) :- functor(T, pair, 2).
        """;

    [Theory]
    [InlineData("compound(N, A), N == pair, A == 3.")]
    [InlineData("atomic_name(N, A), N == hello, A == 0.")]
    [InlineData("number_name(N, A), N == 42, A == 0.")]
    [InlineData("checks(R), R == yes.")]
    [InlineData("rejects(R), R == no.")]
    [InlineData("rejects_name(R), R == no.")]
    [InlineData("alist(N, A), N == '.', A == 2.")]
    [InlineData("built(T), T = pair(_, _).")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>And it really is inline: a decomposing call leaves no
    /// builtin request behind. Without this the tests above would pass just
    /// as well with the builtin doing all the work.</summary>
    [DiagFact]
    public void ADecomposingCallDoesNotLeaveTheModule()
    {
        Assert.Equal(0L, FunctorExitsOf("compound(N, A), A == 3."));
    }

    /// <summary>The counterproof, in red: CONSTRUCTING still leaves. Without
    /// it the test above would also pass with functor/3 refused wholesale,
    /// or inlined for shapes it has no business answering.</summary>
    [DiagFact]
    public void AConstructingCallStepsAsideAtItsOwnGuard()
    {
        Assert.Equal(0L, FunctorExitsOf("built(T), T = pair(_, _)."));
        Assert.True(WasmTierDelegate.DiagMetaGuardHist[29] > 0,
            "the constructing mode must step aside at guard 29, not be "
            + "answered inline");
    }

    /// <summary>Builtin requests for functor/3 in one warm run of the goal.
    /// The first run is the one that promotes, so it is not the one counted.
    /// </summary>
    private long FunctorExitsOf(string goal)
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(goal).Success);

        long f = 0;
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            if (n == "functor" && a == 3) f = hits;
        o.WriteLine($"functor/3 exits={f} deopts={WasmTierDelegate.DiagDeopts}");
        return f;
    }
}
