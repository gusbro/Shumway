using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 47: IL Execute (tail call to a user-defined predicate). The
/// IL emits a Pc-set + B0-update + IlTailCallPending flag, and the
/// outer interpreter's Call/Execute handlers honour the flag by
/// leaving Pc alone — the dispatch then continues at the tail-call
/// target instead of returning to the caller's Cp.
///
/// <para>Non-tail Call (with subsequent goals in the body) and
/// get_structure / put_structure for compound arguments stay
/// outside the IL subset for now — both require continuation
/// machinery that the IL ABI doesn't yet have.</para>
/// </summary>
public class Chunk47Tests
{
    private static Term Atom(string n) => new AtomTerm(n);

    private static int FunctorId(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void Tier0_Baseline_Works()
    {
        // Tier-0 sanity check: without any IL promotion, p/1's body
        // dispatch to q/1 must work. If this fails, the bug isn't in
        // IL — it's in the WAM compilation or Tier-0 dispatch.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public p/1.\n" +
            ":- public q/1.\n" +
            "q(red). q(green). q(blue).\n" +
            "p(X) :- q(X).\n");
        Assert.True(engine.Query("p(red).").Success);
        Assert.False(engine.Query("p(orange).").Success);
    }

    [Fact]
    public void IlExecute_SingleTailCallToUserPredicate()
    {
        // `p(X) :- q(X).` — last call is a tail-call to q/1.
        // q/1 is single-clause with a fact body, so it's IL-promotable.
        // p/1's body has a single Call (which compiles to Execute as the
        // last goal). IL now promotes p/1 too.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public p/1.\n" +
            ":- public q/1.\n" +
            "q(red). q(green). q(blue).\n" +
            "p(X) :- q(X).\n");
        // First call warms p/1.
        Assert.True(engine.Query("p(red).").Success);
        Assert.True(engine.IlPromotion.IsPromoted(FunctorId("p", 1)));
        // Subsequent ground / unbound queries still work.
        Assert.True(engine.Query("p(green).").Success);
        Assert.False(engine.Query("p(orange).").Success);
    }

    [Fact]
    public void IlExecute_BacktrackingEnumeratesViaCallee()
    {
        // p(X) :- q(X). q/1 has three clauses. Tail-calling q from p
        // means external backtracking through p picks up q's
        // alternatives via the normal CP mechanism.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public p/1.\n" +
            ":- public q/1.\n" +
            "q(red). q(green). q(blue).\n" +
            "p(X) :- q(X).\n");
        engine.Query("p(red).");   // warm + promote
        var sols = engine.QueryAll("p(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Atom("red"), Atom("green"), Atom("blue") }, sols);
    }

    [Fact]
    public void IlExecute_MixesWithBuiltinsInBody()
    {
        // p(X) :- atom(X), q(X). q/1 facts.
        // The body has a builtin call (atom/1) followed by an Execute (q/1).
        // Both paths go through IL.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public p/1.\n" +
            ":- public q/1.\n" +
            "q(known). q(other).\n" +
            "p(X) :- atom(X), q(X).\n");
        Assert.True(engine.Query("p(known).").Success);
        Assert.True(engine.IlPromotion.IsPromoted(FunctorId("p", 1)));
        Assert.False(engine.Query("p(42).").Success);   // atom check fails
        Assert.False(engine.Query("p(unknown).").Success);
    }

    [Fact]
    public void IlExecute_ProducesSameResultsAsTier0()
    {
        var src =
            ":- public dispatch/1.\n" +
            ":- public route/1.\n" +
            "route(get). route(post). route(put).\n" +
            "dispatch(R) :- route(R).\n";

        var tier0 = new PrologEngine();
        tier0.ConsultString(src);
        var tier0Sols = tier0.QueryAll("dispatch(X).").Select(s => s["X"]).ToList();

        var tier1 = new PrologEngine();
        tier1.IlPromotion.Threshold = 1;
        tier1.ConsultString(src);
        tier1.Query("dispatch(get).");   // warm + promote
        var tier1Sols = tier1.QueryAll("dispatch(X).").Select(s => s["X"]).ToList();

        Assert.Equal(tier0Sols, tier1Sols);
    }

    [Fact]
    public void IlExecute_RuleWithNonTailCall_StaysOnTier0()
    {
        // p(X) :- q(X), r(X). The body has TWO user-pred calls — the
        // first is a non-tail Call whose callee is multi-clause, so
        // the IL Call helper's leaf-only restriction still rejects
        // p/1.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public p/1.\n" +
            ":- public q/1.\n" +
            ":- public r/1.\n" +
            "q(a). q(b).\n" +
            "r(a). r(b).\n" +
            "p(X) :- q(X), r(X).\n");
        engine.Query("p(a).");
        Assert.True(engine.IlPromotion.IsUnpromotable(FunctorId("p", 1)));
        // Tier-0 still produces the right answers.
        Assert.True(engine.Query("p(a).").Success);
        Assert.False(engine.Query("p(c).").Success);
    }
}
