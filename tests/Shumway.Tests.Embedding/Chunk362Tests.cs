using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 362 (Phase 28): the Tier-1 IL fact inliner's profitability heuristic
/// and its default-on flip. The inliner (chunks 358–360) merges a multi-clause
/// FACT's clause dispatch into a hot caller's IL method, eliminating the
/// trampoline. Chunk 361 wrongly left it OFF on a noisy full-bench reading;
/// chunk 362 establishes that across the van-Roy set only crypt (win) and
/// chat_parser (within noise) ever inline, then adds two principled gates so the
/// default can flip ON safely:
///   1. index-eligibility — only facts whose every clause has a distinct
///      constant first arg (the Phase-1b index pre-filter makes a bound call
///      deterministic); a non-index fact would inline as pure linear-chain bloat;
///   2. a size budget — inline only when clauseCount*(arity+1) ≤ InlineCostBudget,
///      so wide facts (chat_parser's grammar facts) stay on the trampoline.
///
/// These tests pin the OUTCOME that matters: correct answers (incl. backtracking)
/// with the inliner on by default, under forced Tier-1 promotion.
/// </summary>
public class Chunk362Tests
{
    [Fact]
    public void InlineFacts_DefaultsOn()
        => Assert.True(IlPredicateCompiler.InlineFacts);

    [Fact]
    public void InlineCostBudget_KeepsCrypt_ExcludesGrammarFacts()
    {
        // crypt's generators (arity 1, 4–5 clauses) sit at or below the budget;
        // chat_parser's grammar facts (arity 2–3, 6–9 clauses) sit above it.
        Assert.True(5 * (1 + 1) <= IlPredicateCompiler.InlineCostBudget); // odd/even: 10
        Assert.True(4 * (1 + 1) <= IlPredicateCompiler.InlineCostBudget); // lefteven: 8
        Assert.False(3 * (3 + 1) <= IlPredicateCompiler.InlineCostBudget); // arity-3, 3 cl: 12
        Assert.False(9 * (2 + 1) <= IlPredicateCompiler.InlineCostBudget); // arity-2, 9 cl: 27
    }

    // A crypt-shaped, index-eligible small fact (distinct integer first args)
    // called from a hot caller — the inliner's target. Forcing promotion makes
    // the caller Tier-1 IL so the inline fires; the answers must be identical to
    // the trampoline, including the full backtracking enumeration.
    private const string DigitsProgram = @"
:- public pick/2.
d(0). d(2). d(4). d(6). d(8).
pick(X, Y) :- d(X), d(Y).
";

    [Fact]
    public void InlinedFact_BoundFirstArg_IsDeterministicAndCorrect()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;     // force Tier-1 so the inline fires
        e.ConsultString(DigitsProgram);
        // Warm the caller so it promotes, then a bound-arg call: d(4) holds, d(5)
        // does not — the index pre-filter must route each correctly.
        for (int i = 0; i < 3; i++) e.Query("pick(2, 4).");
        Assert.True(e.Query("pick(4, 6).").Success);
        Assert.False(e.Query("pick(5, 6).").Success); // 5 not a digit key
        Assert.False(e.Query("pick(4, 5).").Success);
    }

    [Fact]
    public void InlinedFact_Backtracking_EnumeratesAllSolutions()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(DigitsProgram);
        for (int i = 0; i < 3; i++) e.Query("pick(0, 0)."); // promote the caller

        // Unbound: the inlined fact's clause chain (with CPs into the caller's
        // cursor space) must generate every (X,Y) pair, 5 × 5 = 25, in order.
        var pairs = new List<(long, long)>();
        foreach (var s in e.QueryAll("pick(X, Y)."))
            pairs.Add((((Shumway.Compiler.Ast.IntTerm)s["X"]!).Value,
                       ((Shumway.Compiler.Ast.IntTerm)s["Y"]!).Value));

        Assert.Equal(25, pairs.Count);
        Assert.Equal((0, 0), pairs.First());
        Assert.Equal((8, 8), pairs.Last());
        Assert.Equal(pairs, pairs.Distinct().ToList()); // no duplicates
        // Mixed bound/unbound first arg still enumerates the second fully.
        Assert.Equal(5, e.QueryAll("pick(6, Y).").Count());
    }

    // A wide fact (arity 2, > budget clauses) is ABOVE the size budget, so the
    // inliner is a no-op — it still must produce correct answers via the
    // trampoline.
    private const string WideFactProgram = @"
:- public lookup/2.
w(a, 1). w(b, 2). w(c, 3). w(d, 4). w(e, 5).
w(f, 6). w(g, 7). w(h, 8). w(i, 9). w(j, 10).
lookup(K, V) :- w(K, V).
";

    [Fact]
    public void WideFact_AboveBudget_TrampolineStillCorrect()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(WideFactProgram);
        for (int i = 0; i < 3; i++) e.Query("lookup(a, _).");
        Assert.True(e.Query("lookup(g, 7).").Success);
        Assert.False(e.Query("lookup(g, 8).").Success);
        Assert.Equal(10, e.QueryAll("lookup(_, _).").Count());
    }
}
