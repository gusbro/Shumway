using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 42: the IL compiler now emits multi-clause predicates whose
/// bytecode is the standard <c>switch_on_term + switch_on_atom + clause
/// bodies</c> shape that the WAM produces for predicates like
/// <c>color(red). color(green). color(blue).</c>. The ground-A1 path
/// dispatches by atom id directly; the unbound-A1 path enumerates each
/// clause via the IL choice-point machinery from ADR-014.
/// </summary>
public class Chunk42Tests
{
    private static Term Atom(string n) => new AtomTerm(n);

    [Fact]
    public void MultiClause_GroundQuery_DispatchesViaIL()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            :- public color/1.
            color(red).
            color(green).
            color(blue).
            """);

        // First query: ground arg, hits the IL atom-id dispatch.
        Assert.True(engine.Query("color(red).").Success);
        Assert.True(engine.Query("color(green).").Success);
        Assert.True(engine.Query("color(blue).").Success);
        Assert.False(engine.Query("color(purple).").Success);
    }

    [Fact]
    public void MultiClause_VarQuery_EnumeratesViaILChoicePoints()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            :- public color/1.
            color(red).
            color(green).
            color(blue).
            """);

        // Warm the predicate so it gets IL-promoted.
        engine.Query("color(red).");
        // Now query with unbound X — the IL emits the var-dispatch path
        // that pushes IL CPs for each subsequent clause.
        var sols = engine.QueryAll("color(X).").ToList();
        Assert.Equal(3, sols.Count);
        Assert.Equal(Atom("red"), sols[0]["X"]);
        Assert.Equal(Atom("green"), sols[1]["X"]);
        Assert.Equal(Atom("blue"), sols[2]["X"]);
    }

    [Fact]
    public void MultiClause_PromotedPredicate_FindallStillWorks()
    {
        // findall iterates the promoted predicate via its IL var-dispatch
        // path. The collected list must match what Tier 0 would produce.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            :- public color/1.
            color(red).
            color(green).
            color(blue).
            """);
        engine.Query("color(red).");

        var sol = engine.Query("findall(X, color(X), L).");
        Assert.True(sol.Success);
        // L = [red, green, blue].
        Assert.Equal(
            new CompoundTerm(".", new Term[] {
                Atom("red"),
                new CompoundTerm(".", new Term[] {
                    Atom("green"),
                    new CompoundTerm(".", new Term[] {
                        Atom("blue"), Atom("[]")
                    })
                })
            }),
            sol["L"]);
    }

    [Fact]
    public void MultiClause_TwoClauseIndexed_AlsoCompiles()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            :- public truth/1.
            truth(yes).
            truth(no).
            """);
        engine.Query("truth(yes).");
        var sols = engine.QueryAll("truth(X).").ToList();
        Assert.Equal(2, sols.Count);
    }

    [Fact]
    public void MultiClause_GroundMissingArg_FailsCleanly()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            :- public p/1.
            p(a).
            p(b).
            p(c).
            """);
        engine.Query("p(a).");   // warm + promote
        Assert.False(engine.Query("p(z).").Success);
    }

    [Fact]
    public void MultiClause_ProducesSameResultsAsTier0()
    {
        // Same query against a fresh PrologEngine (Tier 0 only) and a
        // promoted engine — every solution and its order must match.
        var tier0 = new PrologEngine();
        tier0.ConsultString("""
            :- public q/1.
            q(one).
            q(two).
            q(three).
            """);
        var tier0Sols = tier0.QueryAll("q(X).").Select(s => s["X"]).ToList();

        var tier1 = new PrologEngine();
        tier1.IlPromotion.Threshold = 1;
        tier1.ConsultString("""
            :- public q/1.
            q(one).
            q(two).
            q(three).
            """);
        tier1.Query("q(one).");   // warm + promote
        var tier1Sols = tier1.QueryAll("q(X).").Select(s => s["X"]).ToList();

        Assert.Equal(tier0Sols, tier1Sols);
    }
}
