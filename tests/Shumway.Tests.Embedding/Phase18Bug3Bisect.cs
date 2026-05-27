using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Bisection harness for the IL-emit bug Phase 17 surfaced: a
/// scenario that asserts a 2-arity dynamic compound and then calls
/// retractall on a different 1-arity dynamic raises
/// instantiation_error inside retract. Phase 17's PE-patch path is
/// correct; the underlying IL emit produces wrong code for this
/// shape. These tests narrow down WHICH ingredient triggers it so
/// the fix can target the minimum reproducer.
/// </summary>
public class Phase18Bug3Bisect
{
    [Fact]
    public void Baseline_OneAssertOneRetractall_Works()
    {
        Run(":- public scenario/0.\n"
            + ":- dynamic fact/1.\n"
            + "scenario :- assertz(fact(a)), retractall(fact(_)).\n");
    }

    [Fact]
    public void TwoAsserts_NoSecondDyn_Works()
    {
        Run(":- public scenario/0.\n"
            + ":- dynamic fact/1.\n"
            + "scenario :- assertz(fact(a)), assertz(fact(b)), retractall(fact(_)).\n");
    }

    [Fact]
    public void OneAssert_PlusTwoAritySepDyn_Works()
    {
        Run(":- public scenario/0.\n"
            + ":- dynamic fact/1.\n"
            + ":- dynamic pair/2.\n"
            + "scenario :- assertz(fact(a)), assertz(pair(x, y)), retractall(fact(_)).\n");
    }

    [Fact]
    public void TwoAsserts_PlusTwoArityAfterRetractall_Works()
    {
        Run(":- public scenario/0.\n"
            + ":- dynamic fact/1.\n"
            + ":- dynamic pair/2.\n"
            + "scenario :- assertz(fact(a)), assertz(fact(b)), "
            + "retractall(fact(_)), assertz(pair(x, y)).\n");
    }

    [Fact]
    public void TwoAsserts_OneArityPair_Works()
    {
        Run(":- public scenario/0.\n"
            + ":- dynamic fact/1.\n"
            + ":- dynamic pair/1.\n"
            + "scenario :- assertz(fact(a)), assertz(fact(b)), "
            + "assertz(pair(x)), retractall(fact(_)).\n");
    }

    /// <summary>Just clause 1 alone.</summary>
    [Fact]
    public void OneClause_TwoElem_Works()
    {
        Run(":- public main/1.\n"
            + "helper1(X, _) :- ground(X).\n"
            + "main([F, L|_]) :- helper1(F, L).\n");
    }

    /// <summary>Clauses 1+3 (no clause 2).</summary>
    [Fact]
    public void TwoClauses_ManyAndEmpty()
    {
        Run(":- public main/1.\n"
            + "helper1(X, _) :- ground(X).\n"
            + "main([F, L|_]) :- helper1(F, L).\n"
            + "main([]) :- !.\n");
    }

    /// <summary>Clauses 1+2 (no empty list clause).</summary>
    [Fact]
    public void TwoClauses_ManyAndOne()
    {
        Run(":- public main/1.\n"
            + "helper1(X, _) :- ground(X).\n"
            + "helper2(X) :- ground(X).\n"
            + "main([F, L|_]) :- helper1(F, L).\n"
            + "main([F]) :- helper2(F).\n");
    }

    /// <summary>The actual Blint shape — three-clause local entry
    /// promoted to public by Phase 18, list-pattern first arg, calls
    /// other predicates from each clause body.</summary>
    [Fact]
    public void ThreeClauseListEntry_CallsHelpers_Reproducer()
    {
        Run(":- public main/1.\n"
            + "helper1(X, _) :- ground(X).\n"
            + "helper2(X) :- ground(X).\n"
            + "main([F, L|_]) :- helper1(F, L).\n"
            + "main([F]) :- helper2(F).\n"
            + "main([]) :- !.\n");
    }

    [Fact]
    public void TwoAsserts_TwoArityPair_ShouldFail()
    {
        // This is the reproducer.
        Run(":- public scenario/0.\n"
            + ":- dynamic fact/1.\n"
            + ":- dynamic pair/2.\n"
            + "scenario :- assertz(fact(a)), assertz(fact(b)), "
            + "assertz(pair(x, y)), retractall(fact(_)).\n");
    }

    private static void Run(string src)
    {
        var bundle = new Bundle(new[] { new BundleEntry("bug3", src) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var rt = BundleReader.FromBytes(bytes);
        var engine = new PrologEngine();
        engine.LoadBundle(rt);
        // Different entry-pred per shape. ThreeClauseListEntry uses
        // main([1,2]) — exercises the indexed-list dispatch on a list
        // that fits the first clause.
        string query = src.Contains(":- public main/1.")
            ? "main([1, 2])."
            : "scenario.";
        Assert.True(engine.Query(query).Success);
    }
}
