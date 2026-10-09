using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>element/3, global_cardinality/2, circuit/1, tuples_in/2 and
/// labeling with min(Expr) / max(Expr). Every labeled solution set equals a
/// brute-force enumeration of the same relation; the propagation cases and
/// the solution orders are what Scryer's clpz answers.</summary>
public sealed class ClpfdGlobalConstraintsTests
{
    private const string Program = """
        :- use_module(library(clpfd)).

        count_of([], _, 0).
        count_of([X|Xs], K, C) :- count_of(Xs, K, C0), ( X =:= K -> C is C0 + 1 ; C = C0 ).

        el_fd(L) :-
            findall([N,X,Y,V], ( [X,Y] ins 0..3, N in 0..4, V in 0..4,
                                 element(N, [X,2,Y], V), label([N,X,Y,V]) ), L0),
            msort(L0, L).
        el_brute(L) :-
            findall([N,X,Y,V], ( between(0, 3, X), between(0, 3, Y), between(0, 4, N),
                                 between(0, 4, V), nth1(N, [X,2,Y], E), E =:= V ), L0),
            msort(L0, L).

        gcc_fd(L) :-
            findall(Vs-[A,B,C], ( Vs = [_,_,_,_], A in 0..2,
                                  global_cardinality(Vs, [1-A, 3-B, 4-C]),
                                  append(Vs, [A,B,C], All), label(All) ), L0),
            msort(L0, L).
        gcc_key(V) :- member(V, [1,3,4]).
        gcc_brute(L) :-
            findall(Vs-[A,B,C], ( Vs = [_,_,_,_], maplist(gcc_key, Vs),
                                  count_of(Vs, 1, A), A =< 2, count_of(Vs, 3, B),
                                  count_of(Vs, 4, C) ), L0),
            msort(L0, L).

        single_cycle(L) :- length(L, N), A =.. [s|L], cycle_walk(A, 1, N, 0).
        cycle_walk(A, I, N, K) :-
            arg(I, A, J), K1 is K + 1,
            ( J =:= 1 -> K1 =:= N ; K1 < N, cycle_walk(A, J, N, K1) ).

        circ_fd(N, L) :-
            findall(Vs, ( length(Vs, N), circuit(Vs), label(Vs) ), L0), msort(L0, L).
        circ_brute(N, L) :-
            numlist(1, N, Is),
            findall(Vs, ( permutation(Is, Vs), single_cycle(Vs) ), L0), msort(L0, L).

        rel([[1,2,3],[2,2,1],[3,1,1],[1,3,2],[2,1,3]]).
        tup_fd(L) :-
            rel(R),
            findall([X,Y,Z,W], ( [X,Y,Z,W] ins 1..3, X #\= W,
                                 tuples_in([[X,Y,Z],[Z,Y,W]], R), label([X,Y,Z,W]) ), L0),
            msort(L0, L).
        tup_brute(L) :-
            rel(R),
            findall([X,Y,Z,W], ( member([X,Y,Z], R), member([Z,Y,W], R), X =\= W ), L0),
            msort(L0, L).

        % The optimisation order: the plain labeling order, stably sorted by
        % the key Expr gives (negated for max).
        opt_case(Vs, X + 2*Y - Z) :- Vs = [X,Y,Z], Vs ins 0..2, X #\= Z.
        opt_fd(Dir, L) :-
            findall(Vs, ( opt_case(Vs, E), O =.. [Dir, E], labeling([O], Vs) ), L).
        opt_brute(Dir, L) :-
            findall(K-Vs, ( opt_case(Vs, E), label(Vs), K0 is E,
                            ( Dir == min -> K = K0 ; K is -K0 ) ), P),
            keysort(P, S), pairs_values(S, L).
        """;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(Program);
        return e;
    }

    private static void Holds(string goal)
        => Assert.True(Engine().Query(goal + ".").Success, goal);

    [Theory]
    [InlineData("el")]
    [InlineData("gcc")]
    [InlineData("tup")]
    public void LabeledSolutionsAreTheRelation(string constraint)
        => Holds($"{constraint}_fd(F), {constraint}_brute(B), B = [_|_], F == B");

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 6)]
    [InlineData(5, 24)]
    public void CircuitLabelsEverySingleCycle(int n, int cycles)
        => Holds($"circ_fd({n}, F), circ_brute({n}, B), length(B, {cycles}), F == B");

    [Theory]
    [InlineData("min")]
    [InlineData("max")]
    public void OptimisationOrderIsStableByExpr(string dir)
        => Holds($"opt_fd({dir}, F), opt_brute({dir}, B), length(B, 18), F == B");

    [Theory]
    [InlineData("element(N, [3,5,7], V), V #> 4, fd_dom(N, D)", "D", "2..3")]
    [InlineData("element(N, [3,5,7], V), N in 1..2, fd_dom(V, D)", "D", @"3 \/ 5")]
    [InlineData("element(2, [3,5,7], V)", "V", "5")]
    [InlineData("X in 1..2, Y in 5..6, element(N, [X,Y], V), V #> 3, fd_dom(N, D)", "D", "2..2")]
    [InlineData("X in 1..9, element(N, [X,4], V), N = 1, V = 6", "X", "6")]
    [InlineData("Vs = [_,_,_], global_cardinality(Vs, [1-_, 2-_]), Vs = [X|_], fd_dom(X, D)", "D", "1..2")]
    [InlineData("global_cardinality([X, Y], [1-2]), P = X-Y", "P", "1-1")]
    [InlineData("global_cardinality([1, 2, X], [1-A, 2-B]), X = 2, P = A-B", "P", "1-2")]
    [InlineData("global_cardinality([X, Y], [1-1, 2-1]), X = 2", "Y", "1")]
    [InlineData("global_cardinality([_,_,_], [1-A, 2-B]), A = 1", "B", "2")]
    [InlineData("circuit([X])", "X", "1")]
    [InlineData("circuit([X, Y, Z]), fd_dom(X, D)", "D", "2..3")]
    [InlineData("circuit([X, Y, _]), X = 2, fd_dom(Y, D)", "D", "3..3")]
    [InlineData("tuples_in([[X, Y]], [[1,2],[2,3],[3,1]]), X = 2", "Y", "3")]
    [InlineData(@"tuples_in([[X, Y]], [[1,2],[2,3],[3,1]]), Y #\= 3, fd_dom(X, D)", "D", @"1 \/ 3")]
    public void Propagates(string goal, string variable, string expected)
        => Holds($"{goal}, {variable} == ({expected})");

    [Theory]
    [InlineData("element(0, [3,5,7], _)")]
    [InlineData("element(_, [], _)")]
    [InlineData("global_cardinality([X, _], [1-_, 2-_]), X = 3")]
    [InlineData("tuples_in([[_, _]], [[1,2,3]])")]
    [InlineData("tuples_in([[_]], [])")]
    [InlineData("circuit([1, 2])")]
    [InlineData("circuit([2, 1, _])")]
    public void Fails(string goal)
        => Assert.False(Engine().Query(goal + ".").Success, goal);

    [Fact]
    public void AnEmptyCircuitHolds() => Holds("circuit([])");

    [Theory]
    [InlineData(@"[X,Y] ins 1..3, X #\= Y, labeling([min(X+Y)], [X,Y])",
        "[[1,2],[2,1],[1,3],[3,1],[2,3],[3,2]]")]
    [InlineData("[X,Y] ins 1..3, labeling([max(X*Y)], [X,Y])",
        "[[3,3],[2,3],[3,2],[2,2],[1,3],[3,1],[1,2],[2,1],[1,1]]")]
    [InlineData("[X,Y] ins 1..3, labeling([ff, min(X-Y)], [X,Y])",
        "[[1,3],[1,2],[2,3],[1,1],[2,2],[3,3],[2,1],[3,2],[3,1]]")]
    [InlineData("[X,Y] ins 1..3, labeling([down, max(X-Y)], [X,Y])",
        "[[3,1],[3,2],[2,1],[3,3],[2,2],[1,1],[2,3],[1,2],[1,3]]")]
    [InlineData("[X,Y] ins 1..3, labeling([max(X-Y)], [Y,X])",
        "[[3,1],[2,1],[3,2],[1,1],[2,2],[3,3],[1,2],[2,3],[1,3]]")]
    public void OptimisationOrderIsScryers(string goal, string order)
        => Holds($"findall([X,Y], ({goal}), L), L == {order}");

    [Fact]
    public void SeveralOptimisationsAreLexicographic()
        => Holds("findall([X,Y,Z], ([X,Y,Z] ins 0..2, X #\\= Z, labeling([min(X), max(Y+Z)], [X,Y,Z])), L), "
            + "L == [[0,2,2],[0,1,2],[0,2,1],[0,0,2],[0,1,1],[0,0,1],[1,2,2],[1,1,2],[1,0,2],[1,2,0],[1,1,0],[1,0,0],"
            + "[2,2,1],[2,1,1],[2,2,0],[2,0,1],[2,1,0],[2,0,0]]");

    [Theory]
    [InlineData("[X,Y] ins 1..2, Z in 0..1, labeling([min(X+Z)], [X,Y])", "instantiation_error")]
    [InlineData("element(_, [a, b], _)", "type_error(integer, a)")]
    [InlineData("global_cardinality([_], [1-_, 1-_])", "domain_error(gcc_unique_key_pairs, _)")]
    [InlineData("global_cardinality([_], [_-1])", "instantiation_error")]
    [InlineData("tuples_in([[_]], [[a]])", "type_error(integer, a)")]
    public void Raises(string goal, string error)
        => Holds($"catch(({goal}, fail), error({error}, _), true)");

    [Theory]
    [InlineData("element(N, [1,2,3], V), copy_term([N,V], [N,V], Gs)", "element(N, [1,2,3], V)")]
    [InlineData("global_cardinality([A,B], [1-K, 2-M]), copy_term([A,B,K,M], [A,B,K,M], Gs)",
        "global_cardinality([A,B], [1-K, 2-M])")]
    [InlineData("circuit([A,B,C]), copy_term([A,B,C], [A,B,C], Gs)", "circuit([A,B,C])")]
    [InlineData("tuples_in([[X,Y]], [[1,2],[2,3]]), copy_term([X,Y], [X,Y], Gs)", "tuples_in([[X,Y]], [[1,2],[2,3]])")]
    public void APendingGlobalReadsAsWritten(string goal, string written)
        => Holds($"{goal}, memberchk(({written}), Gs)");

    /// <summary>The counts add up to the number of variables, and neither
    /// circuit/1 nor global_cardinality/2 leaves a helper constraint behind
    /// in the answer: only what was written, plus the domains.</summary>
    [Theory]
    [InlineData("global_cardinality([A,B], [1-K, 2-M]), copy_term([A,B,K,M], [A,B,K,M], Gs)")]
    [InlineData("circuit([A,B,C]), copy_term([A,B,C], [A,B,C], Gs)")]
    public void NoHelperConstraintIsProjected(string goal)
        => Holds($"{goal}, \\+ (member(G, Gs), \\+ G = (_ in _), \\+ G = global_cardinality(_, _), \\+ G = circuit(_))");
}
