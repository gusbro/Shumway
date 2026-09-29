using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Tier-1 allocates exactly the heap cells Tier-0 does. A unify_value
/// in read mode against a value that is not an atom or a small integer used to
/// claim a throwaway cell on the IL path only, which put 40% more heap on
/// permutation code and made heap cells, the deterministic metric, disagree
/// between tiers.</summary>
public sealed class IlHeapCellParityTests
{
    private const string Corpus = """
        sel(X, [X|T], T).
        sel(X, [H|T], [H|R]) :- sel(X, T, R).
        perm([], []).
        perm(L, [X|P]) :- sel(X, L, R), perm(R, P).
        same(X, Y, f(X, Y), g(Y)).
        match(f(A, B), [A, B]).
        heads([H|T], H, T).
        pairs([], []).
        pairs([f(A, B)|T], [A-B|R]) :- pairs(T, R).
        keep([H|T], X) :- inner(T, X), H = X.
        inner([Y|_], Y).
        """;

    private static readonly string[] Goals =
    {
        "findall(P, perm([1,2,3,4,5,6], P), _).",
        "findall(X-R, sel(X, [a,b,c,d], R), _).",
        "findall(Z, (between(1, 200, I), same(V, W, T, U), V = I, W = f(I), T = f(_, _), U = g(Z)), _).",
        "findall(L, (between(1, 200, I), match(f(I, g(I)), L)), _).",
        "findall(L, (between(1, 200, I), F is I * 1.5, match(f(F, x), L)), _).",
        // Read through a reference: the arguments are variables bound to the
        // list or structure, so the register holds a REF, not the cell.
        "findall(H-T, (between(1, 200, _), L = [a, b], X = L, heads(X, H, T)), _).",
        "findall(R, (between(1, 200, I), X = [f(I, a), f(b, I)], pairs(X, R)), _).",
        "findall(X, (between(1, 200, I), L = [I, I], keep(L, X)), _).",
    };

    private static string Name(int fid)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        return $"{name[(name.LastIndexOf('$') + 1)..]}/{arity}";
    }

    [Fact]
    public void TierOneAllocatesWhatTierZeroDoes()
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);

        var tiered = new PrologEngine();
        tiered.IlPromotion.Threshold = 1;
        tiered.ConsultString(Corpus);
        string[] expected = { "sel/3", "perm/2", "same/4", "match/2", "heads/3", "pairs/2", "keep/2" };
        var promoted = new HashSet<string>();
        for (int round = 0; round < 5 && !expected.All(promoted.Contains); round++)
        {
            foreach (string g in Goals) tiered.Query(g);
            tiered.IlPromotion.WaitForPendingPromotions();
            promoted = tiered.IlPromotion.PromotedFunctorIds().Select(Name).ToHashSet();
        }
        // ANTI-VACUITY: the predicates the goals exercise did promote.
        foreach (string pi in expected)
            Assert.True(promoted.Contains(pi), $"{pi} did not promote");

        foreach (string g in Goals)
        {
            var answer = plain.Query(g.Replace(", _).", ", Out)."));
            var answerIl = tiered.Query(g.Replace(", _).", ", Out)."));
            Assert.Equal(answer.Success, answerIl.Success);
            Assert.Equal(
                string.Join(", ", answer.Bindings.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {kv.Value}")),
                string.Join(", ", answerIl.Bindings.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {kv.Value}")));
            Assert.True(plain.Query(g).Success, g);
            long t0 = plain.LastQueryCellsAllocated;
            Assert.True(tiered.Query(g).Success, g);
            long t1 = tiered.LastQueryCellsAllocated;
            Assert.True(t0 == t1, $"{g}: Tier-0 {t0} cells, Tier-1 {t1}");
        }
    }
}
