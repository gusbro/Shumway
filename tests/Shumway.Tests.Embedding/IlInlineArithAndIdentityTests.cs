using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Tier-1 evaluates an a_eval sequence on method locals and decides
/// ==/2 without the builtin dispatch. Both take the general path for anything
/// their fast form does not cover; the answers, errors included, must be the
/// interpreter's.</summary>
public sealed class IlInlineArithAndIdentityTests
{
    private const string Corpus = """
        p(X, Y, R) :- R is X + Y * 2.
        q(X, Y, D) :- X =\= Y + D, X =\= Y - D.
        n(X, R) :- R is -X + 1.
        w(X, Y, R) :- R is (X + 1) * (Y - 1).
        v(X, Y, R) :- R = 11, R is X + Y * 2.
        l(X, Y) :- X + 1 < Y * 2.
        e(X, Y) :- X * 2 =:= Y + 0.
        id(A, B, R) :- ( A == B -> R = same ; R = diff ).
        nid(A, B, R) :- ( A \== B -> R = diff ; R = same ).
        k(X, R) :- R is X * 1000 + 7.
        g(X, Y) :- X + Y > 0.
        m(X, Y, R) :- R is X * Y.
        s(A, B, C) :- 1000*A + 100*B + 10*C =:= 10000*A - 8990.
        c(X, Y) :- X * 1000 < Y.
        """;

    // Called from the query, not from a Prolog wrapper: a wrapper would itself
    // promote, and a meta-call from promoted code does not count toward the
    // promotion of what it calls.
    private static string Try(string goal)
        => $"catch(({goal} -> R = yes ; R = no), error(R1, _), R = R1)";

    private static readonly string[] Goals =
    {
        // Integer lane, both 60-bit bounds, and the bignum promotion past them.
        Try("p(1, 2, R0)") + ", X = R0.",
        "M is 2^59 - 1, " + Try("p(M, 1, R0)") + ", X = R0.",
        "M is 2^59 - 1, " + Try("p(1, M, R0)") + ", X = R0.",
        "N is -(2^59), " + Try("n(N, R0)") + ", X = R0.",
        Try("n(5, R0)") + ", X = R0.",
        Try("p(-1, -2, R0)") + ", X = R0.",
        "B is 2^40, " + Try("w(B, B, R0)") + ", X = R0.",
        "M is 2^59 - 1, " + Try("w(M, 3, R0)") + ", X = R0.",
        Try("w(3, 4, R0)") + ", X = R0.",
        // A check per operand, of the width the whole sequence allows: a
        // constant factor narrows it, a product of two operands takes it to 32
        // bits, and a comparison is exact past 60 bits.
        Try("k(5, R0)") + ", X = R0.",
        "B is 2^50, " + Try("k(B, R0)") + ", X = R0.",
        "B is -(2^53), " + Try("k(B, R0)") + ", X = R0.",
        "B is 2^58, " + Try("k(B, R0)") + ", X = R0.",
        "M is 2^59 - 1, " + Try("g(M, M)") + ".",
        "N is -(2^59), " + Try("g(N, N)") + ".",
        "B is 2^31, " + Try("m(B, B, R0)") + ", X = R0.",
        "B is -(2^31), " + Try("m(B, B, R0)") + ", X = R0.",
        "B is 2^31 - 1, C is -B, " + Try("m(B, C, R0)") + ", X = R0.",
        Try("s(1, 0, 1)") + ".", Try("s(1, 1, 0)") + ".",
        "A is 2^50, " + Try("s(A, 1, 2)") + ".",
        // Unchecked, 2^58 * 1000 wraps 64 bits to a negative number, and a
        // comparison has no result check to catch it.
        "B is 2^58, " + Try("c(B, 0)") + ".",
        // The general path: a float, an unbound operand, a non-number.
        Try("p(1.5, 2, R0)") + ", X = R0.",
        Try("p(_, 1, _)") + ".",
        Try("p(a, 1, _)") + ".",
        Try("e(1.5, 3)") + ".",
        // Comparisons and a bound is/2 target.
        Try("q(1, 5, 1)") + ".", Try("q(6, 5, 1)") + ".", Try("q(4, 5, 1)") + ".",
        Try("l(1, 2)") + ".", Try("l(3, 2)") + ".",
        Try("e(3, 6)") + ".", Try("e(3, 7)") + ".",
        Try("v(1, 5, _)") + ".", Try("v(1, 4, _)") + ".",
        // ==/2 and \==/2 over every kind of cell.
        "id(X, X, R).", "id(_, _, R).", "id(a, a, R).", "id(a, b, R).", "id(1, 1, R).",
        "id(1, 1.0, R).", "id(1.5, 1.5, R).", "id(f(Y), f(Y), R).", "id(f(_), f(_), R).",
        "id([1,2], [1,2], R).", "id(X, a, R).", "id([], [], R).",
        "A is 2^100, B is 2^100, id(A, B, R).",
        "put_attr(V, m, 1), id(V, V, R).", "put_attr(V, m, 1), W = V, id(W, V, R).",
        "put_attr(V, m, 1), id(V, _, R).",
        "C = f(C), D = f(D), id(C, D, R).",
        "nid(X, X, R).", "nid(a, b, R).", "nid(f(Y), f(Y), R).", "nid(1, 1.0, R).",
    };

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        if (!r.Success) return "false";
        return string.Join(", ", r.Bindings.Where(kv => kv.Key is "R" or "X")
            .OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {kv.Value}"));
    }

    [Fact]
    public void TheInlineFormsAnswerAsTheInterpreterDoes()
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);

        var tiered = new PrologEngine();
        tiered.IlPromotion.Threshold = 1;
        tiered.ConsultString(Corpus);
        // Promotion is asynchronous: run and drain until every predicate the
        // goals exercise has promoted (ANTI-VACUITY), within a few rounds.
        string[] expected = { "p/3", "q/3", "n/2", "w/3", "v/3", "l/2", "e/2", "id/3", "nid/3",
            "k/2", "g/2", "m/3", "s/3", "c/2" };
        var promoted = new HashSet<string>();
        for (int round = 0; round < 5 && !expected.All(promoted.Contains); round++)
        {
            foreach (string g in Goals) tiered.Query(g);
            Assert.True(tiered.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
            promoted.Clear();
            foreach (int fid in tiered.IlPromotion.PromotedFunctorIds())
            {
                var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
                string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
                promoted.Add($"{name[(name.LastIndexOf('$') + 1)..]}/{arity}");
            }
        }
        foreach (string pi in expected)
            Assert.True(promoted.Contains(pi),
                $"{pi} did not promote\n{tiered.IlPromotion.DescribePromotionState()}");

        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(tiered, g));
        // And the answers are the right ones, not only the same wrong ones.
        foreach (string check in new[] {
            "M is 2^59 - 1, p(M, 1, X), X =:= 2^59 + 1.",
            "N is -(2^59), n(N, X), X =:= 2^59 + 1.",
            "M is 2^59 - 1, w(M, 3, X), X =:= 2^60.",
            "catch(p(_, 1, _), error(instantiation_error, _), true).",
            "q(1, 5, 1).", "\\+ q(6, 5, 1).", "v(1, 5, _).", "\\+ v(1, 4, _).",
            "id(1, 1.0, diff).", "C = f(C), D = f(D), id(C, D, same).",
            "put_attr(V, m, 1), id(V, _, diff).", "id([], [], same).",
            "B is 2^50, k(B, X), X =:= 2^50 * 1000 + 7.",
            "B is 2^58, k(B, X), X =:= 2^58 * 1000 + 7.",
            "M is 2^59 - 1, g(M, M).", "N is -(2^59), \\+ g(N, N).",
            "B is 2^31, m(B, B, X), X =:= 2^62.", "B is -(2^31), m(B, B, X), X =:= 2^62.",
            "s(1, 0, 1).", "\\+ s(1, 1, 0).", "B is 2^58, \\+ c(B, 0)." })
            Assert.True(tiered.Query(check).Success, check);
    }
}
