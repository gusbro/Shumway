using Shumway.Compiler.Ast;
using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 189: expand IL eligibility to multi-clause predicates whose
/// bytecode opens with chunk-67 first/multi-arg indexing
/// (<c>switch_on_term</c> + optional <c>switch_on_arg</c> cascade +
/// final <c>try / retry* / trust</c> chain). Pre-chunk-189 these
/// were rejected by <c>TryDescribeTryMeElseChain</c> (which requires
/// bytecode to open with <c>try_me_else</c>).
///
/// <para>The IL emit doesn't reproduce the switch dispatch — it
/// extracts the clause body ranges from the final var-fallthrough
/// chain and emits the same linear-scan body the chunk-188
/// TryMeElseChain path uses. Correct because each clause body's
/// head-matching opcodes still filter as the switch would have.</para>
/// </summary>
public class Chunk189Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void SwitchOnTerm_AtomKeyedClauses_NowCompiles()
    {
        // Three clauses keyed on first-arg atom. WAM emits
        // switch_on_term + switch_on_atom + final try/retry/trust.
        // Bodies are non-trivial (have body goals) so the
        // IndexedAtomPredicate recogniser (which only takes
        // get_atom+proceed bodies) rejects them.
        var clauses = new ClauseReader(
            "color(red, hot).\n"
            + "color(green, cool).\n"
            + "color(blue, cold).\n").ReadAll().ToList();
        var module = new ModuleCompiler().Compile(clauses);
        var pred = module.Predicates.Single(p => p.FunctorId == Fid("color", 2));
        Assert.True(new IlPredicateCompiler().CanCompile(
            pred, module.Predicates.ToDictionary(p => p.FunctorId)));
    }

    [Fact]
    public void SwitchOnTerm_RunsCorrectly_UnderTier1()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            :- public lookup/2.
            lookup(red, hot).
            lookup(green, cool).
            lookup(blue, cold).
            """);

        Assert.Equal("hot",
            engine.Query("lookup(red, X).").Bindings["X"].ToString());
        Assert.Equal("cool",
            engine.Query("lookup(green, X).").Bindings["X"].ToString());
        Assert.False(engine.Query("lookup(yellow, X).").Success);
    }

    [Fact]
    public void SwitchOnTerm_BodiesWithCallsAndBacktrack()
    {
        // Non-trivial bodies that call other predicates and need
        // backtracking through the indexed path.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            :- public step/2.
            pair(1, a). pair(1, b). pair(2, c).
            step(K, V) :- pair(K, V), atom(V).
            step(_, fallback).
            """);

        // Collect all solutions for step(1, V).
        var sols = engine.QueryAll("step(1, V).")
            .Select(s => s.Bindings["V"].ToString())
            .ToList();
        Assert.Equal(new[] { "a", "b", "fallback" }, sols);
    }

    [Fact]
    public void SwitchOnTerm_IntegerAndAtomMixed()
    {
        // Mix of integer- and atom-keyed clauses → WAM emits
        // switch_on_term with both switch_on_atom and
        // switch_on_integer sub-dispatches.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            :- public tag/2.
            tag(1, one).
            tag(2, two).
            tag(red, color).
            tag(blue, color).
            """);

        Assert.Equal("one",
            engine.Query("tag(1, X).").Bindings["X"].ToString());
        Assert.Equal("color",
            engine.Query("tag(red, X).").Bindings["X"].ToString());
        Assert.False(engine.Query("tag(99, X).").Success);
    }
}
