using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A // B, A div B, A mod B and A rem B as constraints: // truncates,
/// div floors, mod has the sign of B and rem the sign of A, and division by
/// zero fails, as in SICStus and Scryer. The differential compares every
/// labeled solution with what is/2 computes for the same ranges, so pruning
/// that loses a solution, or keeps a wrong one, fails it.</summary>
public sealed class ClpfdDivisionTests
{
    private const string Differential = """
        op_goal(Op, A, B, V, G) :- E =.. [Op, A, B], G = (E #= V).
        eval(Op, A, B, V) :- B =\= 0, E =.. [Op, A, B], V is E.

        same_solutions(Op, AL-AH, BL-BH, VL-VH) :-
            findall(A-B-V, ( A in AL..AH, B in BL..BH, V in VL..VH,
                             op_goal(Op, A, B, V, G), call(G),
                             labeling([], [A, B, V]) ), L1),
            findall(A-B-V, ( between(AL, AH, A), between(BL, BH, B),
                             eval(Op, A, B, V), between(VL, VH, V) ), L2),
            msort(L1, S), msort(L2, S).

        same_solutions_k(Op, AL-AH, K, VL-VH) :-
            findall(A-V, ( A in AL..AH, V in VL..VH, op_goal(Op, A, K, V, G), call(G),
                           labeling([], [A, V]) ), L1),
            findall(A-V, ( between(AL, AH, A), eval(Op, A, K, V), between(VL, VH, V) ), L2),
            msort(L1, S), msort(L2, S).

        some_solutions_k(Op, K) :-
            findall(x, ( between(-20, 20, A), eval(Op, A, K, _) ), L), L \== [].
        """;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).");
        e.ConsultString(Differential);
        return e;
    }

    [Theory]
    [InlineData("//")]
    [InlineData("div")]
    [InlineData("mod")]
    [InlineData("rem")]
    public void AConstantDivisorKeepsExactlyTheSolutions(string op)
    {
        var e = Engine();
        Assert.True(e.Query($"some_solutions_k(({op}), 3).").Success, "nothing here is measured");
        Assert.True(e.Query($"""
            forall(( member(K, [-7, -3, -2, -1, 1, 2, 3, 7]),
                     member(AR, [-20-20, 0-20, -20-0, 3-9, -9-(-3)]),
                     member(VR, [-20-20, 0-3, -3-0, 2-2, -2-(-2)]) ),
                   same_solutions_k(({op}), AR, K, VR)).
            """).Success, $"{op}: a solution was lost or a wrong one kept");
    }

    [Theory]
    [InlineData("//")]
    [InlineData("div")]
    [InlineData("mod")]
    [InlineData("rem")]
    public void AVariableDivisorKeepsExactlyTheSolutions(string op)
    {
        var e = Engine();
        Assert.True(e.Query($"""
            forall(( member(AR, [-12-12, 0-12, -12-(-1)]),
                     member(BR, [-5-5, 1-5, -5-(-1), -2-3]),
                     member(VR, [-12-12, 0-2, -3-(-1)]) ),
                   same_solutions(({op}), AR, BR, VR)).
            """).Success, $"{op}: a solution was lost or a wrong one kept");
    }

    [Theory]
    [InlineData("X in 0..20, X mod 7 #= 2", "2..16")]
    [InlineData("X in -10..10, X mod -3 #= -1", "-10..8")]
    [InlineData("X in -10..10, X rem 3 #= -1", "-10.. -1")]
    [InlineData("X in -10..10, X div 3 #= -2", "-6.. -4")]
    [InlineData("X in -10..10, X // 3 #= -2", "-8.. -6")]
    [InlineData("X in -10..10, X // -3 #= 2", "-8.. -6")]
    [InlineData("X in -10..10, X div -3 #= 2", "-8.. -6")]
    public void TheDividendIsNarrowedToItsExactBounds(string goal, string bounds)
        => Assert.True(Engine().Query($"{goal}, fd_inf(X, L), fd_sup(X, H), L..H == ({bounds}).").Success,
            $"{goal}: not {bounds}");

    [Theory]
    [InlineData("R #= -7 // 2", -3)]
    [InlineData("R #= -7 div 2", -4)]
    [InlineData("R #= -7 mod 2", 1)]
    [InlineData("R #= -7 rem 2", -1)]
    [InlineData("R #= 7 // -2", -3)]
    [InlineData("R #= 7 div -2", -4)]
    [InlineData("R #= 7 mod -2", -1)]
    [InlineData("R #= 7 rem -2", 1)]
    public void GroundOperandsComputeAsIsDoes(string goal, int expected)
        => Assert.True(Engine().Query($"{goal}, R == {expected}.").Success, goal);

    [Theory]
    [InlineData("_ #= 7 // 0")]
    [InlineData("_ #= 7 div 0")]
    [InlineData("_ #= 7 mod 0")]
    [InlineData("_ #= 7 rem 0")]
    [InlineData("X in 0..2, _ #= 6 // X, X = 0")]
    [InlineData("X in 0..2, _ #= 6 mod X, X = 0")]
    public void DivisionByZeroFails(string goal)
        => Assert.False(Engine().Query($"catch(({goal}), _, true).").Success, goal);

    [Fact]
    public void AVariableDivisorLosesItsZero()
        => Assert.True(Engine().Query("X in -2..2, _ #= 6 div X, fd_dom(X, D), D == (-2.. -1 \\/ 1..2).").Success);

    [Theory]
    [InlineData("X in 0..20, X mod 7 #= R", "X mod 7 #= R")]
    [InlineData("X in 0..20, X rem Y #= R, Y in 2..3", "X rem Y #= R")]
    [InlineData("X in 0..20, X div 7 #= R", "X div 7 #= R")]
    [InlineData("X in 0..20, X // 7 #= R", "X // 7 #= R")]
    public void APendingDivisionReadsAsWritten(string goal, string residue)
        => Assert.True(Engine().Query($"{goal}, copy_term([X, R], [X, R], Gs), memberchk(({residue}), Gs).").Success,
            $"{goal}: no {residue} among the residual goals");
}
