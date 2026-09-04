using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The labeling heuristics GNU Prolog documents, implemented over
/// this solver and checked against that engine's measured behaviour: the
/// value orders byte for byte, and the variable selections through the
/// solution sequence they produce (which is what a selection is observable
/// as). The strategies carry both spellings — GNU's
/// variable_method/value_method wrappers on fd_labeling/2, and the
/// SWI-shaped atoms on labeling/2 — over one implementation.</summary>
public class LabelingHeuristicsTests
{
    private static PrologEngine Clpfd()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.True(e.Query("use_module(library(clpfd)).").Success);
        return e;
    }

    private static void True(string goal) => Assert.True(Clpfd().Query(goal).Success);

    [Theory]
    // Measured on GNU Prolog 1.5.0: the enumeration order of one variable's
    // domain under each value method. `middle` walks outward from the
    // midpoint, the lower value first on a tie, and the midpoint of an
    // even-sized domain falls between two values.
    [InlineData("min", 1, 7, "[1,2,3,4,5,6,7]")]
    [InlineData("max", 1, 7, "[7,6,5,4,3,2,1]")]
    [InlineData("middle", 1, 7, "[3,4,2,5,1,6,7]")]
    [InlineData("middle", 1, 8, "[4,3,5,2,6,1,7,8]")]
    [InlineData("middle", 0, 6, "[2,3,1,4,0,5,6]")]
    [InlineData("middle", 2, 5, "[3,2,4,5]")]
    // bisect splits the domain rather than trying values, so for a single
    // free variable it still comes out ascending — as it does there.
    [InlineData("bisect", 1, 7, "[1,2,3,4,5,6,7]")]
    public void TheValueOrderMatchesTheReference(string method, int lo, int hi, string expected)
        => True($"fd_domain(X, {lo}, {hi}), "
                + $"findall(X, fd_labeling([X], [value_method({method})]), L), "
                + $"L == {expected}.");

    [Fact]
    public void RandomOffersEveryValueExactlyOnce()
    {
        // The order is not reproducible; the set is.
        True("fd_domain(X, 1, 7), "
             + "findall(X, fd_labeling([X], [value_method(random)]), L), "
             + "msort(L, S), S == [1,2,3,4,5,6,7].");
    }

    [Theory]
    // A variable selection shows up as the order the solutions come in.
    // Measured on GNU Prolog with A in 1..3 (size 3, min 1, max 3) and
    // B in 5..6 (size 2, min 5, max 6): the methods split into those that
    // pick A and those that pick B, and ours split the same way.
    [InlineData("standard", "[1-5,1-6,2-5,2-6,3-5,3-6]")]
    [InlineData("smallest", "[1-5,1-6,2-5,2-6,3-5,3-6]")]
    [InlineData("max_regret", "[1-5,1-6,2-5,2-6,3-5,3-6]")]
    [InlineData("first_fail", "[1-5,2-5,3-5,1-6,2-6,3-6]")]
    [InlineData("ff", "[1-5,2-5,3-5,1-6,2-6,3-6]")]
    [InlineData("largest", "[1-5,2-5,3-5,1-6,2-6,3-6]")]
    [InlineData("most_constrained", "[1-5,2-5,3-5,1-6,2-6,3-6]")]
    public void TheVariableSelectionMatchesTheReference(string method, string expected)
        => True("fd_domain(A, 1, 3), fd_domain(B, 5, 6), "
                + $"findall(A-B, fd_labeling([A,B], [variable_method({method})]), L), "
                + $"L == {expected}.");

    [Fact]
    public void RandomSelectionStillEnumeratesEverySolution()
    {
        True("fd_domain(A, 1, 3), fd_domain(B, 5, 6), "
             + "findall(A-B, fd_labeling([A,B], [variable_method(random)]), L), "
             + "msort(L, S), S == [1-5,1-6,2-5,2-6,3-5,3-6].");
    }

    [Theory]
    // Whatever the heuristic, the answer SET is the same: a heuristic
    // reorders the search, it does not change what is true.
    [InlineData("variable_method(standard)")]
    [InlineData("variable_method(ff)")]
    [InlineData("variable_method(most_constrained)")]
    [InlineData("variable_method(smallest)")]
    [InlineData("variable_method(largest)")]
    [InlineData("variable_method(max_regret)")]
    [InlineData("value_method(min)")]
    [InlineData("value_method(max)")]
    [InlineData("value_method(middle)")]
    [InlineData("value_method(bisect)")]
    public void EveryHeuristicIsComplete(string option)
        => True("fd_domain([X,Y], 1, 3), X #< Y, "
                + $"findall(X-Y, fd_labeling([X,Y], [{option}]), L), "
                + "msort(L, S), S == [1-2,1-3,2-3].");

    [Fact]
    public void BoundsIsRefusedAsTheReferenceRefusesIt()
    {
        // GNU documents value_method(bounds) and its own implementation
        // rejects it, so accepting it would mean inventing an order no
        // reference defines.
        True("fd_domain([X], 1, 3), "
             + "catch(fd_labeling([X], [value_method(bounds)]), error(E, _), true), "
             + "E = domain_error(fd_labeling_option, value_method(bounds)).");
    }

    [Fact]
    public void BacktracksCountsNothingWhenNothingIsRetried()
    {
        True("fd_domain([A,B], 1, 3), A #> B, "
             + "fd_labeling([A,B], [backtracks(N)]), N == 0.");
    }

    [Fact]
    public void BacktracksCountsARealSearch()
    {
        // Eight queens has to backtrack; the number is this engine's own
        // work (propagation strength decides it), so what is pinned is that
        // it is counted at all and that first-fail does no more of it.
        var e = Clpfd();
        e.ConsultString("""
            :- public queens/3.
            safe([]).
            safe([X|Y]) :- noattack(X, Y, 1), safe(Y).
            noattack(_, [], _).
            noattack(X, [Y|Z], N) :-
                X #\= Y, X #\= Y+N, X #\= Y-N, N1 is N+1, noattack(X, Z, N1).
            queens(N, M, B) :-
                length(L, N), fd_domain(L, 1, N), safe(L),
                fd_labeling(L, [variable_method(M), backtracks(B)]), !.
            """);
        Assert.True(e.Query("queens(8, standard, B), integer(B), B > 0.").Success);
        Assert.True(e.Query(
            "queens(8, standard, B1), queens(8, first_fail, B2), B2 =< B1.").Success);
    }

    [Fact]
    public void ANestedLabelingDoesNotCorruptAnOuterCount()
    {
        // The counter's key is fresh per call, so an inner enumeration is
        // not charged to the outer one.
        True("fd_domain([A,B], 1, 3), A #> B, "
             + "fd_labeling([A,B], [backtracks(_Inner)]), "
             + "fd_domain([C,D], 1, 2), C #> D, "
             + "fd_labeling([C,D], [backtracks(Outer)]), Outer == 0.");
    }

    [Theory]
    // The same strategies under the spellings labeling/2 uses.
    [InlineData("labeling([ffc], [X,Y])")]
    [InlineData("labeling([min], [X,Y])")]
    [InlineData("labeling([max], [X,Y])")]
    [InlineData("labeling([bisect], [X,Y])")]
    [InlineData("labeling([ff, bisect], [X,Y])")]
    public void TheSwiSpellingsReachTheSameStrategies(string call)
        => True($"[X,Y] ins 1..3, X #< Y, findall(X-Y, {call}, L), "
                + "msort(L, S), S == [1-2,1-3,2-3].");

    [Fact]
    public void BisectHandlesAWideDomain()
    {
        // The reason bisect exists: splitting reaches a value without
        // walking the domain.
        True("X in 1..1000000, X #> 999998, "
             + "findall(X, labeling([bisect], [X]), L), L == [999999, 1000000].");
    }
}
