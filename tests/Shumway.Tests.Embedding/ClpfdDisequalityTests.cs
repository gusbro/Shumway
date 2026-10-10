using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The disequalities: X #\= Y, the linear ones, and abs(X - Y) #\= C.
/// They act only once a variable is fixed, so a domain that shrinks without
/// becoming a value does not wake them; binding or aliasing does. Aliasing
/// two variables a disequality keeps apart has to fail, and abs(X - Y) #\= C
/// takes both values at distance C out of the other side.</summary>
public sealed class ClpfdDisequalityTests
{
    private const string Queens = """
        queens_abs(N, Qs) :- length(Qs, N), Qs ins 1..N, safe_abs(Qs), labeling([ff], Qs).
        safe_abs([]).
        safe_abs([Q|Qs]) :- no_attack_abs(Q, Qs, 1), safe_abs(Qs).
        no_attack_abs(_, [], _).
        no_attack_abs(Q, [Q1|Qs], D) :-
            Q #\= Q1, abs(Q - Q1) #\= D, D1 is D + 1, no_attack_abs(Q, Qs, D1).

        queens_lin(N, Qs) :- length(Qs, N), Qs ins 1..N, safe_lin(Qs), labeling([ff], Qs).
        safe_lin([]).
        safe_lin([Q|Qs]) :- no_attack_lin(Q, Qs, 1), safe_lin(Qs).
        no_attack_lin(_, [], _).
        no_attack_lin(Q, [Q1|Qs], D) :-
            Q #\= Q1, Q #\= Q1 + D, Q #\= Q1 - D, D1 is D + 1, no_attack_lin(Q, Qs, D1).
        """;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).");
        e.ConsultString(Queens);
        return e;
    }

    private static bool Holds(string goal) => Engine().Query(goal + ".").Success;

    [Theory]
    [InlineData(@"X in 1..3, Y in 1..3, X #\= Y, X = Y")]
    [InlineData("X in 1..3, Y in 1..3, all_different([X, Y]), X = Y")]
    [InlineData(@"X in 1..9, Y in 1..9, X - Y #\= 0, X = Y")]
    [InlineData(@"X in 1..9, Y in 1..9, abs(X - Y) #\= 0, X = Y")]
    [InlineData(@"X in 1..9, Y in 0..9, Z in 1..9, X + Y - Z #\= 0, X = Z, Y = 0")]
    public void AliasingWhatADisequalityKeepsApartFails(string goal)
        => Assert.False(Holds(goal), goal);

    [Theory]
    [InlineData(@"X in 1..9, Y in 1..9, abs(X - Y) #\= 3, X = Y")]
    [InlineData(@"X in 1..9, Y in 1..9, X - Y #\= 2, X = Y")]
    public void AliasingThatSatisfiesItSucceeds(string goal)
        => Assert.True(Holds(goal), goal);

    [Theory]
    [InlineData(@"X in 1..9, Y in 1..9, abs(X - Y) #\= 2, X = 5", "Y", @"1..2 \/ 4..6 \/ 8..9")]
    [InlineData(@"X in 1..9, Y in 1..9, abs(X - Y) #\= 2, Y = 5", "X", @"1..2 \/ 4..6 \/ 8..9")]
    [InlineData(@"X in 1..9, Y in 1..9, 2 #\= abs(X - Y), X = 5", "Y", @"1..2 \/ 4..6 \/ 8..9")]
    [InlineData(@"X in 1..9, abs(X - 3) #\= 2", "X", @"2..4 \/ 6..9")]
    [InlineData(@"X in 1..9, abs(3 - X) #\= 0", "X", @"1..2 \/ 4..9")]
    [InlineData(@"X in 1..9, Y in 1..9, abs(X - Y) #\= -1, X = 5", "Y", "1..9")]
    public void AbsDiffTakesBothValuesOut(string goal, string variable, string domain)
        => Assert.True(Holds($"{goal}, fd_dom({variable}, D), D == ({domain})"), goal);

    [Fact]
    public void APendingAbsDiffReadsAsWritten()
        => Assert.True(Holds("X in 1..9, Y in 1..9, abs(X - Y) #\\= 2, copy_term([X, Y], [X, Y], Gs), memberchk((abs(X - Y) #\\= 2), Gs)"));

    [Fact]
    public void ADecidedAbsDiffIsLeftToTheDomain()
        => Assert.True(Holds("X in 1..9, Y in 1..9, abs(X - Y) #\\= 2, X = 5, copy_term(Y, Y, Gs), Gs == [Y in 1..2 \\/ 4..6 \\/ 8..9]"));

    [Theory]
    [InlineData("queens_abs", 6, 4)]
    [InlineData("queens_abs", 8, 92)]
    [InlineData("queens_lin", 6, 4)]
    [InlineData("queens_lin", 8, 92)]
    public void QueensFindsEverySolutionOnce(string queens, int n, int solutions)
        => Assert.True(Holds($"findall(Qs, {queens}({n}, Qs), L), length(L, {solutions}), sort(L, S), length(S, {solutions})"),
            $"{queens}({n}) should have {solutions} distinct solutions");

    [Fact]
    public void TwentyFourQueensIsSolvedByPropagation()
        => Assert.True(Holds("queens_abs(24, Qs), Qs = [_|_]"));
}
