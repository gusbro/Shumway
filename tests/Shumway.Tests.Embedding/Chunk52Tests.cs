using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 52: DCG enhancements (<c>peek/1</c> lookahead and
/// <c>pushback/1</c>) plus Tier-1 IL emission for non-indexed
/// multi-clause predicates (<c>try_me_else</c> chain shape).
/// </summary>
public class Chunk52Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    private static int FunctorId(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    // ============================================================================
    // DCG: peek/1 (lookahead)
    // ============================================================================

    [Fact]
    public void Dcg_Peek_DoesNotConsumeInput()
    {
        // sniff --> peek(a), [a].   First peeks 'a' (no consumption),
        // then actually consumes 'a'. Residue is whatever's left.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public sniff/2.\n" +
            "sniff --> peek(a), [a].\n");
        // Input [a, b, c] → peek(a) verifies head=a (no consume), then
        // [a] consumes 'a'. Residue is [b, c].
        var sol = engine.Query("sniff([a, b, c], R).");
        Assert.True(sol.Success);
        Assert.Equal(
            new CompoundTerm(".", new Term[] { Atom("b"),
                new CompoundTerm(".", new Term[] { Atom("c"), Atom("[]") }) }),
            sol["R"]);
    }

    [Fact]
    public void Dcg_Peek_FailsOnWrongHead()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public must_start_a/2.\n" +
            "must_start_a --> peek(a).\n");
        Assert.True(engine.Query("must_start_a([a, b], R), R == [a, b].").Success);
        Assert.False(engine.Query("must_start_a([b, c], _).").Success);
    }

    // ============================================================================
    // DCG: pushback/1 (extend output)
    // ============================================================================

    [Fact]
    public void Dcg_Pushback_ExtendsResidueWithLiteralTokens()
    {
        // wrap --> [open], pushback([close]). Consumes 'open', then
        // pushes [close] back onto the output state. For input
        // [open, x], the residue after wrap is [close, x].
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public wrap/2.\n" +
            "wrap --> [open], pushback([close]).\n");
        var sol = engine.Query("wrap([open, x], R).");
        Assert.True(sol.Success);
        Assert.Equal(
            new CompoundTerm(".", new Term[] { Atom("close"),
                new CompoundTerm(".", new Term[] { Atom("x"), Atom("[]") }) }),
            sol["R"]);
    }

    [Fact]
    public void Dcg_PushbackEmpty_NoOp()
    {
        // pushback([]) doesn't change the diff-list state.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public bypass/2.\n" +
            "bypass --> [t], pushback([]).\n");
        Assert.True(engine.Query("bypass([t, u], R), R == [u].").Success);
    }

    // ============================================================================
    // IL multi-clause non-indexed (try_me_else chain)
    // ============================================================================

    [Fact]
    public void Il_TryMeElseChain_ArityZeroMultiClausePromotes()
    {
        // Multi-clause arity-0 predicate: WAM emits try_me_else / trust_me
        // with each clause body being a bare proceed. Three clauses
        // means three "succeed" alternatives.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public ok/0.\nok.\nok.\nok.\n");
        // First query warms.
        Assert.True(engine.Query("ok.").Success);
        Assert.True(engine.IlPromotion.IsPromoted(FunctorId("ok", 0)));
        // QueryAll should yield three solutions.
        Assert.Equal(3, engine.QueryAll("ok.").Count());
    }

    [Fact]
    public void Il_TryMeElseChain_MultiClauseVarFirstArgPromotes()
    {
        // p(X) :- atom(X).
        // p(X) :- integer(X).
        // Each clause has a var first arg (no indexing); body is a
        // single builtin call. The try_me_else chain wraps two clauses.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public p/1.\n" +
            "p(X) :- atom(X).\n" +
            "p(X) :- integer(X).\n");
        Assert.True(engine.Query("p(foo).").Success);
        Assert.True(engine.Query("p(42).").Success);
        Assert.False(engine.Query("p(3.14).").Success);
        Assert.True(engine.IlPromotion.IsPromoted(FunctorId("p", 1)));
    }

    [Fact]
    public void Il_TryMeElseChain_BacktrackingEnumeratesClauses()
    {
        // Three clauses, all succeed for any var input — backtracking
        // visits each in source order via the IL choice-point machinery.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public any/1.\n" +
            "any(_).\n" +
            "any(_).\n" +
            "any(_).\n");
        engine.Query("any(x).");   // warm
        Assert.Equal(3, engine.QueryAll("any(y).").Count());
    }

    [Fact]
    public void Il_TryMeElseChain_ProducesSameResultsAsTier0()
    {
        var src =
            ":- public pick/1.\n" +
            "pick(X) :- atom(X).\n" +
            "pick(X) :- number(X).\n";

        var tier0 = new PrologEngine();
        tier0.ConsultString(src);
        var sol0 = tier0.QueryAll("(X = foo ; X = 7), pick(X).").Count();

        var tier1 = new PrologEngine();
        tier1.IlPromotion.Threshold = 1;
        tier1.ConsultString(src);
        tier1.Query("pick(foo).");   // warm
        var sol1 = tier1.QueryAll("(X = foo ; X = 7), pick(X).").Count();

        Assert.Equal(sol0, sol1);
    }
}
