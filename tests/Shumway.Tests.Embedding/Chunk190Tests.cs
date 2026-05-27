using Shumway.Compiler.Ast;
using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 190: extend <see cref="IlPredicateCompiler"/>'s
/// indexed-atom recogniser + emitter to accept clauses with
/// arbitrary IL-supported body opcodes — not just the trivial
/// <c>get_atom + proceed</c> shape from chunk 52. The bytecode
/// shape (<c>switch_on_term</c> + <c>switch_on_atom</c>) stays the
/// same; each clause's body simply runs via
/// <see cref="EmitClauseBody"/> on a ground match or var-enter
/// rather than the inline "unify + return true" shortcut.
///
/// <para>The speed-up over the chunk-189 <c>SwitchedChain</c>
/// linear-scan fallback comes from the cmp-chain ground dispatch:
/// for a predicate with K clauses keyed on distinct atoms,
/// ground-A1 lookup is O(K) cmp-eq instructions instead of O(K)
/// head-match-and-fail-and-restore-and-trail tries.</para>
/// </summary>
public class Chunk190Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void IndexedAtom_NonTrivialBody_NowCompilesViaIndexedPath()
    {
        // arity-1 atom-keyed clauses with real bodies. Pre-chunk-190
        // these fell through to TryDescribeSwitchedChain (linear scan).
        // Now they match TryDescribeIndexedAtomPredicate and get the
        // ground-cmp-chain dispatch.
        var clauses = new ClauseReader(
            "step(red) :- write(red_state).\n"
            + "step(green) :- write(green_state).\n"
            + "step(blue) :- write(blue_state).\n").ReadAll().ToList();
        var module = new ModuleCompiler().Compile(clauses);
        var pred = module.Predicates.Single(p => p.FunctorId == Fid("step", 1));
        Assert.True(new IlPredicateCompiler().CanCompile(
            pred, module.Predicates.ToDictionary(p => p.FunctorId)));
    }

    [Fact]
    public void IndexedAtom_NonTrivialBody_GroundDispatchPicksRightClause()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public lookup/2.\n"
            + "lookup(red, X) :- X = hot.\n"
            + "lookup(green, X) :- X = cool.\n"
            + "lookup(blue, X) :- X = cold.\n");

        Assert.Equal("hot",
            engine.Query("lookup(red, X).").Bindings["X"].ToString());
        Assert.Equal("cool",
            engine.Query("lookup(green, X).").Bindings["X"].ToString());
        Assert.Equal("cold",
            engine.Query("lookup(blue, X).").Bindings["X"].ToString());
    }

    [Fact]
    public void IndexedAtom_NonTrivialBody_VarDispatchEnumerates()
    {
        // X unbound → var dispatch should walk every clause via CPs.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public step/2.\n"
            + "step(a, 1).\n"
            + "step(b, 2).\n"
            + "step(c, 3).\n");

        var sols = engine.QueryAll("step(K, V).")
            .Select(s => (K: s.Bindings["K"].ToString(),
                          V: s.Bindings["V"].ToString()))
            .ToList();
        Assert.Equal(3, sols.Count);
        Assert.Contains(("a", "1"), sols);
        Assert.Contains(("b", "2"), sols);
        Assert.Contains(("c", "3"), sols);
    }

    [Fact]
    public void IndexedAtom_NonTrivialBody_WithNonLeafCall()
    {
        // Body contains a non-tail Call to a multi-clause callee.
        // Tests the chunk-182 threading runs correctly inside the
        // chunk-190 emit.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            "helper(1, one).\n"
            + "helper(2, two).\n"
            + ":- public translate/2.\n"
            + "translate(en, V) :- helper(1, V).\n"
            + "translate(es, V) :- helper(2, V).\n");

        Assert.Equal("one",
            engine.Query("translate(en, X).").Bindings["X"].ToString());
        Assert.Equal("two",
            engine.Query("translate(es, X).").Bindings["X"].ToString());
        Assert.False(engine.Query("translate(fr, X).").Success);
    }

    [Fact]
    public void IndexedAtom_TrivialBody_StillCompilesAndRuns()
    {
        // Regression: the trivial body shape (chunk 52) still works
        // through the rewritten emit.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public color/1.\n"
            + "color(red). color(green). color(blue).\n");

        Assert.True(engine.Query("color(red).").Success);
        Assert.True(engine.Query("color(green).").Success);
        Assert.False(engine.Query("color(yellow).").Success);
    }
}
