using Shumway.Compiler.Il;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-061: an alternatives method compiles at its first use and a
/// cold method at its first call, so a test that never reaches one never has
/// the JIT check its IL. Here every continuation method compiles at install
/// (<see cref="IlPredicateCompiler.CpsCompileEveryMethod"/>), and the answers
/// are the interpreter's.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class CpsEveryMethodCompilesTests : IDisposable
{
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;

    public CpsEveryMethodCompilesTests()
    {
        IlPredicateCompiler.CpsMode = true;
        IlPredicateCompiler.CpsCompileEveryMethod = true;
    }

    public void Dispose()
    {
        IlPredicateCompiler.CpsCompileEveryMethod = false;
        IlPredicateCompiler.CpsMode = _savedCpsMode;
    }

    // digit/1 is backtracked into; kind/2's choice points are cut away by its
    // callers; walk/3 is deterministic; grow/2 reaches big integers.
    private const string Corpus = """
        digit(0). digit(1). digit(2). digit(3). digit(4).
        pair(X, Y) :- digit(X), digit(Y), X + Y =:= 5.
        kind(X, small) :- X < 3.
        kind(X, large) :- X >= 3.
        first_kind(X, K) :- kind(X, K), !.
        walk([], Acc, Acc).
        walk([X|Xs], Acc0, Acc) :- first_kind(X, K), walk(Xs, [X-K|Acc0], Acc).
        grow(0, 1) :- !.
        grow(N, F) :- N1 is N - 1, grow(N1, F1), F is F1 * 1000003.
        """;

    private static readonly string[] Goals =
    {
        "findall(X-Y, pair(X, Y), R).",
        "walk([0, 4, 2, 3], [], R).",
        "first_kind(1, R).",
        "grow(12, R).",
        "findall(F, (digit(D), grow(D, F)), R).",
    };

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        return r.Success ? $"R = {r["R"]}" : "false";
    }

    [Fact]
    public void EveryContinuationMethodCompilesAndAnswersAsTheInterpreterDoes()
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);

        int cold0 = IlPredicateCompiler.CpsCompiledColdMethods;
        int alt0 = IlPredicateCompiler.CpsCompiledAlternativesMethods;
        var tiered = new PrologEngine();
        tiered.IlPromotion.Threshold = 1;
        tiered.ConsultString(Corpus);
        for (int round = 0; round < 3; round++)
        {
            foreach (string g in Goals) tiered.Query(g);
            Assert.True(tiered.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        // ANTI-VACUITY: cold and alternatives methods compiled at install.
        Assert.True(IlPredicateCompiler.CpsCompiledColdMethods > cold0, "no cold method compiled");
        Assert.True(IlPredicateCompiler.CpsCompiledAlternativesMethods > alt0, "no alternatives method compiled");

        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(tiered, g));
    }
}
