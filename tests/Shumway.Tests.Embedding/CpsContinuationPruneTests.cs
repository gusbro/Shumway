using Shumway.Compiler.Il;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-061: a continuation method whose clause ends without a branch
/// back to the predicate's entry is emitted from its resume point only. The
/// answers are the interpreter's: past an inline if-then-else's condition,
/// in a later clause of a chain, after an indexed guard's commit, and with a
/// self tail call that keeps the whole body.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class CpsContinuationPruneTests : IDisposable
{
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;

    public CpsContinuationPruneTests() => IlPredicateCompiler.CpsMode = true;

    public void Dispose() => IlPredicateCompiler.CpsMode = _savedCpsMode;

    // step/2 has two clauses, so a call to it stays a call.
    private const string Corpus = """
        step(X, Y) :- X >= 0, Y is X + 1.
        step(X, Y) :- X < 0, Y is X - 1.
        big(A, R) :- step(A, B), step(B, C),
            ( C > 2 -> step(C, D), step(D, E) ; E = C ),
            step(E, F), atom_chars(Z, [z]), step(F, G),
            R = r(B, C, E, F, G, Z).
        pick(0, zero).
        pick(N, R) :- N > 0, step(N, M), atom_length(abc, L), step(M, P), R = pos(M, L, P).
        pick(N, neg) :- N < 0.
        count(0, Acc, Acc) :- !.
        count(N, Acc0, Acc) :- step(Acc0, Acc1), N1 is N - 1, count(N1, Acc1, Acc).
        cls(X, small) :- X < 10, !, step(X, Y), Y > 0.
        cls(X, R) :- step(X, Y), step(Y, Z), R = big(Y, Z).
        """;

    private static readonly string[] Goals =
    {
        "big(0, R).", "big(5, R).", "big(-3, R).",
        "findall(B-R, (member(B, [0, 5, -3]), big(B, R)), R).",
        "pick(0, R).", "pick(3, R).", "pick(-2, R).",
        "findall(N-R, (member(N, [0, 3, -2]), pick(N, R)), R).",
        "count(500, 0, R).",
        "cls(5, R).", "cls(20, R).", "findall(R, cls(20, R), R).", "findall(R, cls(5, R), R).",
    };

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        return r.Success ? $"R = {r["R"]}" : "false";
    }

    private static string Name(int fid)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        return $"{name[(name.LastIndexOf('$') + 1)..]}/{arity}";
    }

    [Fact]
    public void APrunedContinuationAnswersAsTheInterpreterDoes()
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);

        var tiered = new PrologEngine();
        tiered.IlPromotion.Threshold = 1;
        tiered.ConsultString(Corpus);
        string[] expected = { "big/2", "pick/2", "count/3", "cls/2" };
        var promoted = new Dictionary<string, int>();
        for (int round = 0; round < 5 && !expected.All(promoted.ContainsKey); round++)
        {
            foreach (string g in Goals) tiered.Query(g);
            Assert.True(tiered.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
            promoted = tiered.IlPromotion.PromotedFunctorIds().ToDictionary(Name, fid => fid);
        }
        foreach (string pi in expected)
            Assert.True(promoted.ContainsKey(pi), $"{pi} did not promote");
        // ANTI-VACUITY: these predicates run continuation methods emitted from
        // their resume point; count/3's self tail call keeps its whole body.
        int Pruned(string pi) => IlPredicateCompiler.CpsPrunedMethods.TryGetValue(promoted[pi], out int n) ? n : 0;
        foreach (string pi in new[] { "big/2", "pick/2", "cls/2" })
            Assert.True(Pruned(pi) > 0, $"{pi} has no pruned continuation method");
        Assert.Equal(0, Pruned("count/3"));

        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(tiered, g));
    }
}
