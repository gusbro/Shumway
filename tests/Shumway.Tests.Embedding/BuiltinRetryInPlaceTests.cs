using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A backtrackable builtin's choice point is restored and kept by a
/// backtrack, and trusted by its delegate on the last solution. What the pop
/// and push used to give must hold the same: the last solution leaves no
/// choice point, nested enumerations each keep their own, and the frames a
/// retry leaves above the choice point are dead. In Tier-0 and from a promoted
/// predicate.</summary>
public sealed class BuiltinRetryInPlaceTests
{
    private const string Corpus = """
        :- public t/1.
        t(G) :- call(G).
        % An environment frame above the choice point on every retry.
        deep(X, Y) :- Z is X * 2, inner(Z, Y).
        inner(Z, Y) :- Y is Z + 1.
        sum_deep(N, S) :-
            nb_setval(acc, 0),
            ( between(1, N, X), deep(X, Y),
              ( X mod 500 =:= 0 -> garbage_collect ; true ),
              nb_getval(acc, A), A1 is A + Y, nb_setval(acc, A1), fail
            ; true ),
            nb_getval(acc, S).
        """;

    private static PrologEngine Engine(int threshold)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = threshold;
        e.ConsultString(Corpus);
        if (threshold > 0)
        {
            Assert.True(e.Query("t(true).").Success);
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
            Assert.True(e.IlPromotion.IsPromoted(FunctorTable.Intern(AtomTable.Intern("t").Id, 1)),
                "t/1 did not promote: this would be Tier-0 again");
        }
        return e;
    }

    [Theory]
    // The last solution trusts the choice point: the goal exits deterministically.
    [InlineData("t((setup_call_cleanup(true, between(1, 3, X), F = done), X == 3)), F == done.")]
    [InlineData("t((setup_call_cleanup(true, atom_concat(X, _, abc), F = done), X == abc)), F == done.")]
    [InlineData("t((setup_call_cleanup(true, sub_atom(abc, _, 1, _, S), F = done), S == c)), F == done.")]
    // Nested enumerations, each retried in its own frame.
    [InlineData("findall(X-Y, t((between(1, 3, X), between(X, 3, Y))), Ps), "
        + "Ps == [1-1, 1-2, 1-3, 2-2, 2-3, 3-3].")]
    [InlineData("findall(A+B, t((atom_concat(A, B, ab), sub_atom(ab, _, 1, _, A))), Ps), Ps == [a+b].")]
    // A ball thrown from a retried solution.
    [InlineData("catch(t((between(1, 5, X), X >= 3, throw(found(X)))), found(Y), true), Y == 3.")]
    // A cut in the middle prunes the frame.
    [InlineData("t((between(1, 1000000, X), X >= 7, !)), X == 7.")]
    // Frames above the choice point, and collections, between retries.
    [InlineData("t(sum_deep(2000, S)), S == 4004000.")]
    public void RetriesInPlaceAnswerAsBefore(string query)
    {
        foreach (int threshold in new[] { 0, 1 })
            Assert.True(Engine(threshold).Query(query).Success, $"{query} (threshold {threshold})");
    }
}
