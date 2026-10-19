using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A region's inline bind sites (get_constant, get_list and its
/// fused windows) trail the binding of a variable older than HB while the
/// region holds the trail top in a local (ADR-060), and the trail grows
/// under them. Backtracking over such bindings, past the trail's initial
/// capacity, must undo every one of them.</summary>
public sealed class IlInlineTrailTests
{
    // Each walker binds every element of an old list at one inline form: a
    // constant, a list with no live cells, a list of two fresh variables (the
    // var/var window) and a list of a value and a fresh variable (the val/var
    // window). A walk is a loop inside one region method.
    private const string Corpus = """
        walk([]).
        walk([X|Xs]) :- cell(X), walk(Xs).
        cell(a).
        grow([]).
        grow([X|Xs]) :- pair(X), grow(Xs).
        pair([_|_]).
        spread([], _, _).
        spread([X|Xs], H, T) :- first(X, H, T), spread(Xs, H, T).
        first([H|T], H, T).
        thread([], _).
        thread([X|Xs], H) :- cons(H, X, _), thread(Xs, H).
        cons(X, [X|Xs], Xs).
        end([X], X) :- !.
        end([_|Xs], X) :- end(Xs, X).
        """;

    // Past the trail's initial capacity, so that it grows inside the region,
    // more than once.
    private const int N = 3000;

    // The variables are older than the disjunction's choice point: every
    // binding is trailed, and failing back must leave all of them unbound.
    private static string Goal(string body)
        => $"length(L, {N}), {body}, term_variables(L, Vs), length(Vs, K).";

    private static readonly (string Goal, int Unbound)[] Goals =
    {
        (Goal("( walk(L), fail ; true )"), N),
        (Goal("( grow(L), fail ; true )"), N),
        (Goal("( spread(L, h, t), fail ; true )"), N),
        (Goal("( thread(L, h), fail ; true )"), N),
        (Goal("( walk(L), fail ; grow(L) ), end(L, E), E = [_|_]"), 2 * N),
        (Goal("walk(L), end(L, E), E == a"), 0),
        (Goal("spread(L, h, t), end(L, E), E == [h|t]"), 0),
        (Goal("thread(L, h), end(L, [h|E]), var(E)"), N),
    };

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        return r.Success ? $"K = {r.Bindings["K"]}" : "false";
    }

    [Fact]
    public void BacktrackingOverInlineBindingsPastTheTrailsCapacityUndoesThemAll()
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);

        var tiered = new PrologEngine();
        tiered.IlPromotion.Threshold = 1;
        tiered.ConsultString(Corpus);
        // Promotion is asynchronous: run and drain until every walker has
        // promoted (ANTI-VACUITY), within a few rounds.
        string[] expected = { "walk/1", "grow/1", "spread/3", "thread/2" };
        var promoted = new HashSet<string>();
        for (int round = 0; round < 5 && !expected.All(promoted.Contains); round++)
        {
            foreach (var (g, _) in Goals) tiered.Query(g);
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
        foreach (var (g, unbound) in Goals)
        {
            Assert.Equal($"K = {unbound}", Answer(plain, g));
            Assert.Equal($"K = {unbound}", Answer(tiered, g));
        }
    }
}
