using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 50: Tier-1 IL gains the last big pieces of the Phase-1 ABI:
/// non-tail <c>Call</c> (when the callee is a leaf — single-clause,
/// body-less head matching) and the PSTR opcodes <c>get_pstr</c> /
/// <c>put_pstr</c>. Together with chunks 41–49 this brings the IL
/// subset to feature-parity with the dominant rule shapes the WAM
/// compiler produces for Phase-1 programs.
/// </summary>
public class Chunk50Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Pstr(string s) => new StringTerm(s);

    private static int FunctorId(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    // ============================================================================
    // IL Call (non-tail) to a leaf callee
    // ============================================================================

    [Fact]
    public void IlCall_NonTailToLeaf_Promotes()
    {
        // p :- q, r.   q and r are body-less facts (leaf predicates),
        // so the Call to q + Execute r body shape now promotes.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public p/0.\n" +
            ":- public q/0.\n" +
            ":- public r/0.\n" +
            "q. r.\n" +
            "p :- q, r.\n");
        Assert.True(engine.Query("p.").Success);
        Assert.True(engine.IlPromotion.IsPromoted(FunctorId("p", 0)));
    }

    [Fact]
    public void IlCall_MultipleNonTailCalls()
    {
        // chain :- a, b, c. — two Calls (a, b) + Execute (c).
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public chain/0.\n" +
            ":- public a/0.\n" +
            ":- public b/0.\n" +
            ":- public c/0.\n" +
            "a. b. c.\n" +
            "chain :- a, b, c.\n");
        Assert.True(engine.Query("chain.").Success);
        Assert.True(engine.IlPromotion.IsPromoted(FunctorId("chain", 0)));
    }

    [Fact]
    public void IlCall_ToLeafWithHeadArgs()
    {
        // The callee has head args (uses get_atom + proceed). Still a leaf.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public main/1.\n" +
            ":- public check/1.\n" +
            "check(ok).\n" +
            "main(X) :- check(X), check(ok).\n");
        Assert.True(engine.Query("main(ok).").Success);
        Assert.False(engine.Query("main(no).").Success);
        Assert.True(engine.IlPromotion.IsPromoted(FunctorId("main", 1)));
    }

    [Fact]
    public void IlCall_FailingCalleePropagatesFailure()
    {
        // q succeeds for 'ok' only. p calls q with the arg. When the
        // call fails, p's IL must propagate the failure.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public p/1.\n" +
            ":- public q/1.\n" +
            ":- public r/0.\n" +
            "q(ok). r.\n" +
            "p(X) :- q(X), r.\n");
        Assert.True(engine.Query("p(ok).").Success);
        Assert.False(engine.Query("p(fail).").Success);
    }

    [Fact]
    public void IlCall_ToNonLeafCallee_StaysOnTier0()
    {
        // mid :- a, a.  — mid has a body call, so it isn't a leaf.
        // foo :- mid, a. — foo can't IL-promote because the Call to
        // mid wouldn't survive the IL Call's leaf-only restriction.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public foo/0.\n" +
            ":- public mid/0.\n" +
            ":- public a/0.\n" +
            "a.\n" +
            "mid :- a, a.\n" +
            "foo :- mid, a.\n");
        engine.Query("foo.");
        Assert.True(engine.IlPromotion.IsUnpromotable(FunctorId("foo", 0)));
        // Tier-0 still answers correctly.
        Assert.True(engine.Query("foo.").Success);
    }

    [Fact]
    public void IlCall_ProducesSameResultsAsTier0()
    {
        var src =
            ":- public greet/1.\n" +
            ":- public welcome/1.\n" +
            ":- public hello/0.\n" +
            "hello.\n" +
            "welcome(world).\n" +
            "greet(X) :- welcome(X), hello.\n";

        var tier0 = new PrologEngine();
        tier0.ConsultString(src);
        var sol0 = tier0.Query("greet(world).");

        var tier1 = new PrologEngine();
        tier1.IlPromotion.Threshold = 1;
        tier1.ConsultString(src);
        tier1.Query("greet(world).");   // warm
        var sol1 = tier1.Query("greet(world).");

        Assert.Equal(sol0.Success, sol1.Success);
    }

    // ============================================================================
    // IL PSTR opcodes
    // ============================================================================

    [Fact]
    public void IlPstr_HeadMatchOnStringLiteral()
    {
        // greet("hello"). — head match against a PSTR uses get_pstr.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public greet/1.\ngreet(\"hello\").");
        Assert.True(engine.Query("greet(\"hello\").").Success);
        Assert.False(engine.Query("greet(\"world\").").Success);
        Assert.True(engine.IlPromotion.IsPromoted(FunctorId("greet", 1)));
    }

    [Fact]
    public void IlPstr_BindsStringToVarArgument()
    {
        // greet("hello"). — calling with an unbound var: head match in
        // write mode constructs the PSTR on the heap and binds the var.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public greet/1.\ngreet(\"hello\").");
        var sol = engine.Query("greet(S).");
        Assert.True(sol.Success);
        Assert.Equal(Pstr("hello"), sol["S"]);
    }

    [Fact]
    public void IlPstr_MultipleStringLiterals()
    {
        // Two clauses with different string literals — each goes through
        // the string literal pool with a distinct id. The IL emission
        // resolves them via engine.CurrentStringLiterals.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public phrase/1.\n" +
            "phrase(\"hola\").\n" +
            "phrase(\"adios\").\n");
        // Multi-clause, both strings → indexed-atom path doesn't apply
        // (PSTRs aren't atoms). Falls to the try_me_else chain — outside
        // the current IL subset. So both queries answer via Tier 0.
        Assert.True(engine.Query("phrase(\"hola\").").Success);
        Assert.True(engine.Query("phrase(\"adios\").").Success);
        Assert.False(engine.Query("phrase(\"hello\").").Success);
    }
}
