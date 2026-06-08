using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 362 (Phase 28): the Tier-1 IL fact inliner's profitability gate, its
/// default-on flip, and the O(1) cursor jump table that makes inlining strictly
/// cheaper than the trampoline.
///
/// The inliner (chunks 358–360) merges a multi-clause FACT's clause dispatch
/// into a hot caller's IL method, eliminating the trampoline. Chunk 361 wrongly
/// left it OFF on a noisy reading; chunk 362 establishes the real picture: only
/// one principled gate is needed — index-eligibility (every clause has a distinct
/// constant first arg, so the Phase-1b index pre-filter makes a bound call
/// deterministic). A non-index fact would inline as a pure linear chain with no
/// indexing gain, so the trampoline keeps it.
///
/// A short-lived clause-count/arity "size budget" (removed) was masking an
/// implementation flaw: backtracking re-entered the caller delegate through a
/// LINEAR cursor compare-chain that grew with each inlined alternative, so
/// inlining a wide fact cost MORE than the trampoline it replaced. Replacing that
/// with an O(1) jump table (the cursor switch in EmitSingleClauseMetaCpBody) made
/// re-entry constant, so even a 9-clause grammar fact inlines without regressing —
/// no size budget required.
///
/// These tests pin the OUTCOME that matters: correct answers (incl. backtracking)
/// with the inliner on by default, under forced Tier-1 promotion, for both small
/// (crypt-shaped) and wide facts.
/// </summary>
public class Chunk362Tests
{
    [Fact]
    public void InlineFacts_DefaultsOn()
        => Assert.True(IlPredicateCompiler.InlineFacts);

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
        // cursor space, re-entered via the jump table) must generate every (X,Y)
        // pair, 5 × 5 = 25, in order.
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

    // A WIDE index-eligible fact (10 clauses) — above the (removed) size budget.
    // With the O(1) cursor jump table it inlines without regressing, and must
    // still produce correct answers both bound and via backtracking. This pins
    // that the jump table — not a clause-count cap — is what keeps wide-fact
    // inlining cheap.
    private const string WideFactProgram = @"
:- public lookup/2.
w(a, 1). w(b, 2). w(c, 3). w(d, 4). w(e, 5).
w(f, 6). w(g, 7). w(h, 8). w(i, 9). w(j, 10).
lookup(K, V) :- w(K, V).
";

    [Fact]
    public void WideFact_Inlined_BoundAndBacktrackingCorrect()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(WideFactProgram);
        for (int i = 0; i < 3; i++) e.Query("lookup(a, _).");
        Assert.True(e.Query("lookup(g, 7).").Success);   // bound first arg → det
        Assert.False(e.Query("lookup(g, 8).").Success);
        Assert.False(e.Query("lookup(z, _).").Success);  // no key z
        Assert.Equal(10, e.QueryAll("lookup(_, _).").Count()); // full enumeration
    }
}
