using Shumway.Compiler.Ast;
using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Probe: are DCG-compiled predicates already IL-eligible after the
/// chunks 188 / 189 / 190 expansions? A DCG rule like
/// <c>sentence --&gt; noun_phrase, verb_phrase.</c> transforms via
/// <see cref="DcgTransform"/> into an ordinary clause with two
/// extra diff-list args:
/// <c>sentence(S0, S) :- noun_phrase(S0, S1), verb_phrase(S1, S).</c>.
/// After transform there's nothing DCG-specific in the bytecode —
/// it's a regular multi-clause predicate with non-tail Calls.
/// </summary>
public class DcgIlProbeTests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void Dcg_RuleWithConjunction_RunsUnderTier1()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            :- public sentence/2.
            sentence --> noun_phrase, verb_phrase.
            noun_phrase --> [the], [dog].
            noun_phrase --> [a], [cat].
            verb_phrase --> [barks].
            verb_phrase --> [meows].
            """);

        // sentence(Input, []) — full parse, no leftover.
        var sol = engine.Query("sentence([the, dog, barks], []).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Dcg_RuleWithBacktracking_FindsAllParses()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            :- public greeting/2.
            greeting --> [hello].
            greeting --> [hi].
            greeting --> [hey].
            """);

        var sols = engine.QueryAll("greeting(L, []).")
            .Select(s => s.Bindings["L"].ToString())
            .ToList();
        Assert.Equal(3, sols.Count);
    }

    [Fact]
    public void Dcg_RuleWithPrologEscape_RunsUnderTier1()
    {
        // The {G} body form passes G through as a plain Prolog goal
        // (no diff-list threading). The body becomes a non-tail Call
        // to G with no diff-list args.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            :- public digit/2.
            digit(D) --> [D], { D >= 0'0, D =< 0'9 }.
            """);

        var sol = engine.Query("digit(0'5, [0'5], []).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Dcg_CanCompile_DirectCheck()
    {
        // Direct verification: after DcgTransform, the resulting
        // predicate matches one of the IL-eligible shapes.
        var raw = new ClauseReader(
            "sentence --> noun, verb.\n"
            + "noun --> [the], [dog].\n"
            + "noun --> [a], [cat].\n"
            + "verb --> [barks].\n").ReadAll().ToList();
        var transformed = DcgTransform.Apply(raw);
        var module = new ModuleCompiler().Compile(transformed);
        var fid = Fid("sentence", 2);
        var pred = module.Predicates.Single(p => p.FunctorId == fid);
        Assert.True(new IlPredicateCompiler().CanCompile(
            pred, module.Predicates.ToDictionary(p => p.FunctorId)));
    }
}
