using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 215: deep cut (<c>get_level</c> + <c>cut</c>) is now IL-emittable.
/// Previously only <c>neck_cut</c> (a <c>!</c> at the clause neck) was in
/// the IL subset, so every predicate with a <c>!</c> after a goal — the
/// bulk of a Prolog parser/tokenizer — was parked on Tier-0. These tests
/// force Tier-1 promotion (Threshold=1), assert the predicate really
/// compiled to IL, and check the cut semantics match the interpreter:
/// commit + discard later clauses, correct barrier after a sub-goal call
/// (the <c>_b0</c> register is clobbered by the call, so the barrier must
/// come from the Y slot get_level stashed), and a deep cut in a later
/// clause reached by backtracking (the per-CP <c>_b0</c> restore).
/// </summary>
public class Chunk215Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void DeepCut_Commits_DiscardsLaterClauses_UnderTier1()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        // Variable heads + body-computed result keep this a pure
        // TryMeElseChain (no first/second-arg indexing), so the only
        // not-previously-supported opcodes are get_level/cut — isolating
        // the deep-cut feature under test.
        engine.ConsultString(
            ":- public classify/2.\n"
            + "classify(X, R) :- X < 0, !, R = neg.\n"
            + "classify(X, R) :- X =:= 0, !, R = zero.\n"
            + "classify(_, R) :- R = pos.\n");

        // Each input yields exactly one solution — the cut commits.
        Assert.Equal(new[] { "neg" }, Results(engine, "classify(-5, R).", "R"));
        Assert.Equal(new[] { "zero" }, Results(engine, "classify(0, R).", "R"));
        Assert.Equal(new[] { "pos" }, Results(engine, "classify(7, R).", "R"));

        // The predicate actually ran on Tier-1 (deep cut, no neck cut).
        Assert.True(engine.IlPromotion.IsPromoted(Fid("classify", 2)));
    }

    [Fact]
    public void DeepCut_AfterSubgoalCall_UsesEntryBarrier_NotClobberedB0()
    {
        // helper/1 is a Call that overwrites the _b0 register. The cut in
        // g/1 must commit to g's entry barrier (captured by get_level
        // before the call), discarding helper's remaining solutions AND
        // the g(_) clause. If the cut used the clobbered register it would
        // either under- or over-cut.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            "helper(1).\nhelper(2).\nhelper(3).\n"
            + ":- public g/1.\n"
            + "g(X) :- helper(X), X > 0, !.\n"
            + "g(99).\n");

        Assert.Equal(new[] { "1" }, Results(engine, "g(X).", "X"));
        Assert.True(engine.IlPromotion.IsPromoted(Fid("g", 1)));
    }

    [Fact]
    public void DeepCut_InLaterClause_ReachedByBacktracking()
    {
        // Clause 1 fails for every X (no X > 5 in [1,2,3]); execution
        // backtracks into clause 2, whose deep cut must commit using the
        // entry barrier restored from the choice point's saved _b0.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public f/2.\n"
            + "f(X, R) :- member(X, [1,2,3]), X > 5, R = a.\n"
            + "f(X, R) :- member(X, [1,2,3]), X >= 2, !, R = b.\n"
            + "f(_, R) :- R = c.\n");

        // Clause 1: no solution. Clause 2: X=1 (X>=2 fails), X=2 (cut) ->
        // (X=2, b). The cut discards member's CP and clause 3.
        var sols = engine.QueryAll("f(X, R).")
            .Select(s => $"{s.Bindings["X"]}-{s.Bindings["R"]}")
            .ToList();
        Assert.Equal(new[] { "2-b" }, sols);
        Assert.True(engine.IlPromotion.IsPromoted(Fid("f", 2)));
    }

    [Fact]
    public void DeepCut_Tier1_MatchesTier0()
    {
        // Cross-check: the same program with promotion off (Tier-0) must
        // give identical answers.
        const string program =
            ":- public classify/2.\n"
            + "classify(X, R) :- X < 0, !, R = neg.\n"
            + "classify(X, R) :- X =:= 0, !, R = zero.\n"
            + "classify(_, R) :- R = pos.\n";

        var tier0 = new PrologEngine();                 // Threshold defaults to 0 (off)
        tier0.ConsultString(program);
        var tier1 = new PrologEngine();
        tier1.IlPromotion.Threshold = 1;
        tier1.ConsultString(program);

        foreach (var input in new[] { "-3", "0", "42" })
        {
            var q = $"classify({input}, R).";
            Assert.Equal(Results(tier0, q, "R"), Results(tier1, q, "R"));
        }
        Assert.False(tier0.IlPromotion.IsPromoted(Fid("classify", 2)));
        Assert.True(tier1.IlPromotion.IsPromoted(Fid("classify", 2)));
    }

    private static System.Collections.Generic.List<string> Results(
        PrologEngine engine, string query, string var) =>
        engine.QueryAll(query).Select(s => s.Bindings[var].ToString()!).ToList();
}
