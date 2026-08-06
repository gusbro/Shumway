using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// How much a constrained CLP(FD) answer says, and in whose terms.
///
/// <para>A residual answer is only useful if it can be read. Both things it
/// used to say too much of are structural rather than cosmetic: a disequality
/// over a compound (<c>Q #\= R + D</c>) used to invent an auxiliary variable
/// per operator — a variable the user never wrote, printed with its own
/// domain and defining equation — and <c>all_different/1</c> printed the
/// n(n-1)/2 disequalities that implement it rather than itself.</para>
/// </summary>
public sealed class ClpfdProjectionCompactnessTests
{
    private const string Queens = """
        queens_fd(N, Qs) :-
            length(Qs, N), Qs ins 1..N, all_different(Qs), diagonals(Qs).
        diagonals([]).
        diagonals([Q|Qs]) :- no_diag(Q, Qs, 1), diagonals(Qs).
        no_diag(_, [], _).
        no_diag(Q, [R|Rs], D) :- Q #\= R + D, Q #\= R - D, D1 is D + 1, no_diag(Q, Rs, D1).
        """;

    private static PrologEngine Fd(string? program = null)
    {
        var e = new PrologEngine();
        e.UseClpfd();
        if (program is not null) e.ConsultString(program);
        return e;
    }

    private static int GoalCount(PrologEngine e, string setup, string term)
    {
        var s = e.Query($"{setup}, copy_term({term}, _, Gs__), length(Gs__, N).");
        Assert.True(s.Success);
        return (int)Assert.IsType<IntTerm>(s["N"]!).Value;
    }

    [Fact]
    public void ADisequalityOverACompoundInventsNoVariable()
    {
        var e = Fd();
        // Three goals would be the decomposition: the aux variable's domain,
        // `R + D #= _T`, and `Q #\= _T`. One is the constraint itself.
        Assert.Equal(3, GoalCount(e, "[Q, R] ins 1..8, Q #\\= R + 1", "[Q, R]"));
    }

    [Fact]
    public void ADisequalityOverACompoundPrintsAsWritten()
    {
        var e = Fd();
        var s = e.Query("[Q, R] ins 1..8, Q #\\= R + 1, copy_term([Q, R], _, Gs).");
        Assert.True(s.Success);
        string text = AstTermRenderer.Render(s["Gs"]!, 1200, e.Operators);
        Assert.Contains("#\\=", text);
        Assert.DoesNotContain("#=", text.Replace("#\\=", ""));   // no defining equation
    }

    [Fact]
    public void AllDifferentPrintsAsItselfNotAsItsPairs()
    {
        var e = Fd();
        // 4 domains + all_different — not 4 domains + the six disequalities.
        Assert.Equal(5, GoalCount(e, "Vs = [_, _, _, _], Vs ins 1..4, all_different(Vs)", "Vs"));

        var s = e.Query(
            "Vs = [_, _, _, _], Vs ins 1..4, all_different(Vs), copy_term(Vs, _, Gs).");
        Assert.True(s.Success);
        Assert.Contains("all_different",
            AstTermRenderer.Render(s["Gs"]!, 1200, e.Operators));
    }

    [Fact]
    public void EightQueensStaysReadable()
    {
        // The whole point, on the program that surfaced it. The count is a
        // ceiling, not a target: it was 204.
        var e = Fd(Queens);
        Assert.InRange(GoalCount(e, "queens_fd(8, Qs)", "Qs"), 1, 80);
    }

    [Fact]
    public void SolvingIsUnchanged()
    {
        // The projection is a view of the store; changing how the store is
        // built must not change what the program computes.
        var e = Fd(Queens);
        var s = e.Query("findall(Q, (queens_fd(8, Q), label(Q)), L), length(L, N).");
        Assert.True(s.Success);
        Assert.Equal(92L, Assert.IsType<IntTerm>(s["N"]!).Value);

        // One unknown left is decided on the spot, and an impossible one fails.
        Assert.True(e.Query("X in 1..3, Y = 2, X #\\= Y + 1, X #\\= 2, X #= 1.").Success);
        Assert.False(e.Query("X = 1, Y = 1, X #\\= Y + 0.").Success);
        // A repeated variable combines: X #\= 2*X - 3 IS X #\= 3.
        Assert.False(e.Query("X in 0..9, X #\\= 2*X - 3, X #= 3.").Success);
        Assert.True(e.Query("X in 0..9, X #\\= 2*X - 3, X #= 4.").Success);
    }
}
