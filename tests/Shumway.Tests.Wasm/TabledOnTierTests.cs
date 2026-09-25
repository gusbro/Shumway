using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A tabled predicate running on the wasm tier. The tier owns the
/// memory-side choice-point stack and lowers B over it (cut, trust,
/// backtracking between members) without touching the engine's MANAGED
/// parallel IL-choice-point stack. Left unreconciled, a later backtrack to a
/// real IL choice point the wasm buried under a stale entry read the entry's
/// sentinel bp (-1) as a bytecode address: SetPc(-1), which the loop takes as
/// a proceed past the top -- a silent false success. The symptom, from the
/// REPL: fib(30, N) answered "true" with N unbound, and re-asking gave "true"
/// again. clause/2's negation guards plus $table_register's if-then-else were
/// the minimal promoted set that reproduced it.</summary>
public sealed class TabledOnTierTests
{
    private const string Fib = """
        :- table fib/2.
        fib(0, 0).
        fib(1, 1).
        fib(N, F) :-
            N > 1,
            A is N - 1,
            B is N - 2,
            fib(A, FA),
            fib(B, FB),
            F is FA + FB.
        """;

    [Theory]
    [InlineData("fib(3, N).", "2")]
    [InlineData("fib(10, N).", "55")]
    [InlineData("fib(30, N).", "832040")]
    public void ATabledCallBindsItsAnswerOnTheTier(string goal, string want)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Fib);
        Assert.Equal(want, plain.Query(goal).Bindings["N"].ToString());

        var (tiered, _) = TieredEngine.Build(Fib);
        var r = tiered.Query(goal);
        Assert.True(r.Success, "the tiered query failed outright");
        Assert.Equal(want, r.Bindings["N"].ToString());
    }

    /// <summary>The false-success shape directly: a ground call that should
    /// FAIL must not answer true. SetPc(-1) made every tabled query succeed.
    /// </summary>
    [Fact]
    public void AGroundTabledCallThatShouldFailDoesNotFalselySucceed()
    {
        var (tiered, _) = TieredEngine.Build(Fib);
        Assert.True(tiered.Query("fib(10, 55).").Success, "the true case broke");
        Assert.False(tiered.Query("fib(10, 54).").Success,
            "a wrong ground answer succeeded: the -1 bp reads as a proceed");
    }

    /// <summary>The other tabled example on the tier, left-recursive over a
    /// cycle -- the seminaive fixpoint's backtracking stresses the same
    /// managed/memory choice-point boundary.</summary>
    [Fact]
    public void LeftRecursivePathTablesOnTheTier()
    {
        const string P = """
            :- table path/2.
            edge(a, b).  edge(b, c).  edge(c, a).  edge(c, d).
            path(X, Y) :- edge(X, Y).
            path(X, Y) :- path(X, Z), edge(Z, Y).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(P);
        var want = plain.Query(
            "findall(Y, path(a, Y), L), sort(L, S), "
            + "with_output_to(atom(A), writeq(S)).").Bindings["A"].ToString();

        var (tiered, _) = TieredEngine.Build(P);
        var got = tiered.Query(
            "findall(Y, path(a, Y), L), sort(L, S), "
            + "with_output_to(atom(A), writeq(S)).");
        Assert.True(got.Success);
        Assert.Equal(want, got.Bindings["A"].ToString());
    }
}
