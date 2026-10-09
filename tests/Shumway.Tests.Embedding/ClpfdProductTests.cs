using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>X * Y #= Z propagates both ways: each factor is bounded by the
/// product over the other factor, by parts of one sign, and the product by
/// the parts of the factors, so a gap around 0 stays out of it. A square
/// bounds its root. The differential compares every labeled solution with
/// is/2 over the same ranges, so pruning that loses a solution, or keeps a
/// wrong one, fails it.</summary>
public sealed class ClpfdProductTests
{
    private const string Differential = """
        same_products(XD, YD, ZD) :-
            findall(X-Y-Z, ( X in XD, Y in YD, Z in ZD, X * Y #= Z,
                             labeling([], [X, Y, Z]) ), L1),
            findall(X-Y-Z, ( X in XD, Y in YD, Z in ZD, label([X, Y, Z]), Z =:= X * Y ), L2),
            msort(L1, S), msort(L2, S).

        same_squares(XD, ZD) :-
            findall(X-Z, ( X in XD, Z in ZD, X * X #= Z, labeling([], [X, Z]) ), L1),
            findall(X-Z, ( X in XD, Z in ZD, label([X, Z]), Z =:= X * X ), L2),
            msort(L1, S), msort(L2, S).
        """;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).");
        e.ConsultString(Differential);
        return e;
    }

    private static void Holds(string goal)
        => Assert.True(Engine().Query(goal + ".").Success, goal);

    [Fact]
    public void ProductsKeepExactlyTheSolutions()
        => Holds("""
            forall(( member(XD, [-4..4, 0..5, -5.. -1, -3 \/ 3, 1..6]),
                     member(YD, [-4..4, 2..5, -5.. -2, 0..0]),
                     member(ZD, [-20..20, 1..1, 25..25, -6.. -6, 0..0, 7..9]) ),
                   same_products(XD, YD, ZD))
            """);

    [Fact]
    public void SquaresKeepExactlyTheSolutions()
        => Holds("""
            forall(( member(XD, [-8..8, 0..8, -8.. -1, -3 \/ 3]),
                     member(ZD, [0..70, 10..20, 49..49, 2..3, 0..0]) ),
                   same_squares(XD, ZD))
            """);

    [Theory]
    [InlineData("[X,Y] ins 0..5, X*Y #= 25", "X-Y", "5-5")]
    [InlineData("X in 1..12, Y in 5..6, X*Y #= 12", "X-Y", "2-6")]
    [InlineData("X in 0..10, Y in 0..10, X*Y #= 1", "X-Y", "1-1")]
    [InlineData("X in -3..3, Y in -3..3, Z #= X*Y, Z #> 6", "Z", "9")]
    [InlineData("X*X #= C, C in 10..20", "C", "16")]
    // An unbounded factor: Y must be 0 for a product of 0 with X positive.
    [InlineData("X in 1..sup, X*Y #= 0", "Y", "0")]
    public void Decides(string goal, string term, string expected)
        => Holds($"{goal}, {term} == {expected}");

    [Theory]
    [InlineData("X*X #= 49, fd_dom(X, D)", "D", @"-7 \/ 7")]
    [InlineData("X*X #= C, C in 10..20, fd_dom(X, D)", "D", @"-4 \/ 4")]
    [InlineData("[X,Y] ins -5..5, X*Y #= 25, fd_dom(X, D)", "D", @"-5 \/ 5")]
    [InlineData(@"X in -3 \/ 3, Y in -3 \/ 3, Z #= X*Y, fd_dom(Z, D)", "D", @"-9 \/ 9")]
    [InlineData("X in 1..sup, Z in 1..10, Y #= X*Z, fd_dom(Y, D)", "D", "1..sup")]
    [InlineData("X in -2..3, Y #= X*X, fd_dom(Y, D)", "D", "0..9")]
    // A gap in the other factor narrows the quotient: exactly the divisors of 6.
    [InlineData("[X,Y] ins -5..5, X*Y #= -6, fd_dom(X, D)", "D", @"-3.. -2 \/ 2..3")]
    public void Narrows(string goal, string term, string expected)
        => Holds($"{goal}, {term} == ({expected})");

    [Theory]
    [InlineData("[X,Y] ins 0..5, X*Y #= 26")]
    [InlineData("X*X #= 50, X in -7..7")]
    [InlineData("X in 1..sup, Y in 1..sup, X*Y #= 0")]
    public void Fails(string goal) => Assert.False(Engine().Query(goal + ".").Success, goal);
}
