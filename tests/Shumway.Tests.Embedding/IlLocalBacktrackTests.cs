using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-057: a Tier-1 region resumes its own choice points in its
/// failure handler instead of returning to the interpreter. The answers must be
/// the interpreter's in every case the shortcut has to decline or to respect:
/// a sub-goal's backtracking floor (findall/3, \+), nested floors, a throw out
/// of a resumed alternative, a backtrackable builtin's choice point under the
/// region's, and a cut after a resumption.</summary>
public sealed class IlLocalBacktrackTests
{
    private const string Corpus = """
        pick(1). pick(2). pick(3).
        pk(X) :- X = 1.
        pk(X) :- X = 2.
        pk(X) :- X = 3.
        bc(0) :- !.
        bc(N) :- pk(X), X == 3, N1 is N - 1, bc(N1).
        bt(0) :- !.
        bt(N) :- pick(X), X == 3, N1 is N - 1, bt(N1).
        all_pk(L) :- findall(X, pk(X), L).
        none_big :- \+ (pk(X), X > 5).
        nested(L) :- findall(X-L2, (pk(X), findall(Y, (pk(Y), Y < X), L2)), L).
        thrown(R) :- catch((pk(X), X == 2, throw(found(X))), found(R), true).
        with_between(L) :- findall(X-Y, (between(1, 3, X), pk(Y), Y > X), L).
        first_ge2(X) :- pk(X), X >= 2, !.
        count_bc(N, C) :- findall(x, (bc(N) ; bc(1)), L), length(L, C).
        queens(N, Qs) :- numlist(1, N, Ns), perm(Ns, Qs), safe(Qs).
        safe([]).
        safe([Q|Qs]) :- no_attack(Q, Qs, 1), safe(Qs).
        no_attack(_, [], _).
        no_attack(Q, [R|Rs], D) :- Q =\= R + D, Q =\= R - D, D1 is D + 1, no_attack(Q, Rs, D1).
        perm([], []).
        perm(L, [X|Xs]) :- sel(X, L, R), perm(R, Xs).
        sel(X, [X|T], T).
        sel(X, [H|T], [H|R]) :- sel(X, T, R).
        """;

    private static readonly string[] Goals =
    {
        "bc(200).", "bt(200).", "all_pk(L).", "none_big.", "nested(L).",
        "thrown(R).", "with_between(L).", "first_ge2(X).", "count_bc(50, C).",
        "findall(Q, queens(6, Q), L).",
    };

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        if (!r.Success) return "false";
        return string.Join(", ", r.Bindings.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key} = {kv.Value}"));
    }

    [Fact]
    public void RegionsAnswerAsTheInterpreterDoes()
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);

        var tiered = new PrologEngine();
        tiered.IlPromotion.Threshold = 1;
        tiered.ConsultString(Corpus);
        for (int i = 0; i < 2; i++)
            foreach (string g in Goals) tiered.Query(g);
        tiered.IlPromotion.WaitForPendingPromotions();

        // ANTI-VACUITY: the predicates the goals exercise did promote.
        int promoted = tiered.IlPromotion.PromotedFunctorIds().Count();
        Assert.True(promoted >= 10, $"only {promoted} predicates promoted");

        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(tiered, g));
        // And the answers are the right ones, not only the same wrong ones.
        foreach (string check in new[] {
            "nested(L), L == [1-[], 2-[1], 3-[1,2]].", "thrown(R), R == 2.",
            "with_between(L), L == [1-2, 1-3, 2-3].", "first_ge2(X), X == 2.",
            "count_bc(50, C), C == 2.", "findall(Q, queens(6, Q), L), length(L, 4)." })
            Assert.True(tiered.Query(check).Success, check);
    }
}
