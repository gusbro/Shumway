using System;
using System.IO;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A dynamic predicate of facts keeps its compile when the program
/// changes around it, and while it is kept it holds the literals and the
/// atoms its code names (ADR-003): the buffer that held them is gone by the
/// time the next query links the predicate again.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class DynamicCompileCacheTests
{
    private static PrologEngine Engine() => new() { Out = new StringWriter(), Warnings = new StringWriter() };

    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private const string Tables = @"
        :- dynamic(fact_table/2).
        fact_table(a, 1).
        fact_table(b, 2).
        fact_table(c, 3).
        :- dynamic(with_a_rule/1).
        with_a_rule(1).
        with_a_rule(X) :- X > 10.
        ";

    [Fact]
    public void AConsult_KeepsTheCompileOfAPredicateOfFacts_AndNotOfOneWithARule()
    {
        var e = Engine();
        e.ConsultString(Tables);
        Assert.True(e.Query("fact_table(_, _), with_a_rule(_).").Success);
        var facts = e.DynamicPredicateCache[Fid("fact_table", 2)];
        var rule = e.DynamicPredicateCache[Fid("with_a_rule", 1)];
        int derivation = e._derivationGen;

        e.ConsultString("elsewhere(1).");
        Assert.True(e.Query("findall(K-V, fact_table(K, V), L), L == [a-1, b-2, c-3], with_a_rule(11).").Success);

        Assert.NotEqual(derivation, e._derivationGen);
        Assert.Same(facts, e.DynamicPredicateCache[Fid("fact_table", 2)]);
        Assert.NotSame(rule, e.DynamicPredicateCache[Fid("with_a_rule", 1)]);
    }

    [Fact]
    public void AMutation_StillDropsTheCompile()
    {
        var e = Engine();
        e.ConsultString(Tables);
        Assert.True(e.Query("fact_table(_, _).").Success);
        var facts = e.DynamicPredicateCache[Fid("fact_table", 2)];

        Assert.True(e.Query("assertz(fact_table(d, 4)), retract(fact_table(a, _)).").Success);
        e.ConsultString("elsewhere(1).");
        Assert.True(e.Query("findall(K, fact_table(K, _), L), L == [b, c, d].").Success);

        Assert.NotSame(facts, e.DynamicPredicateCache[Fid("fact_table", 2)]);
    }

    // The literals a kept compile names: a string, a float, a big integer and
    // the image of a ground term.
    [Fact]
    public void AKeptCompile_HoldsItsLiterals()
    {
        var e = Engine();
        e.ConsultString(@"
            :- dynamic(lit/2).
            lit(text, ""a string kept in the pool"").
            lit(float, 1.25).
            lit(big, 123456789012345678901234567890).
            lit(term, point(1, [a, b, c], ""inner"")).
            ");
        Assert.True(e.Query("lit(_, _).").Success);
        var kept = e.DynamicPredicateCache[Fid("lit", 2)];

        for (int round = 0; round < 3; round++)
        {
            e.ConsultString($"elsewhere{round}(1).");
            e._literalPools.Sweep();
            Assert.True(e.Query(@"lit(float, F), F =:= 1.25, lit(big, B), B =:= 123456789012345678901234567890,
                lit(text, S), length(S, 25), lit(term, point(1, [a, b, c], _)).").Success, $"round {round}");
            Assert.Same(kept, e.DynamicPredicateCache[Fid("lit", 2)]);
        }
    }

    [Fact]
    public void AKeptCompile_HoldsItsAtoms()
    {
        string s = Guid.NewGuid().ToString("N");
        bool enabled = AtomTable.CollectionEnabled, poisoned = AtomTable.PoisonDeadIds;
        AtomTable.CollectionEnabled = true;
        AtomTable.PoisonDeadIds = true;
        try
        {
            var e = Engine();
            e.ConsultString($":- dynamic(named/2). named(c_{s}, f(g_{s})).");
            Assert.True(e.Query("named(_, _).").Success);
            var kept = e.DynamicPredicateCache[Fid("named", 2)];
            int c = AtomTable.Intern("c_" + s).Id;
            Assert.True(AtomTable.IsTransient(c));

            e.ConsultString("elsewhere(1).");
            AtomTable.RequestCollection();

            Assert.Equal("c_" + s, AtomTable.ProbeById(c)?.Name);
            Assert.True(e.Query($"named(X, f(Y)), X == c_{s}, Y == g_{s}.").Success);
            Assert.Same(kept, e.DynamicPredicateCache[Fid("named", 2)]);
        }
        finally
        {
            AtomTable.PoisonDeadIds = poisoned;
            AtomTable.CollectionEnabled = enabled;
        }
    }

    // A change of tier lays the program out again and transforms nothing.
    [Fact]
    public void AChangeOfTier_LeavesTheDerivation_AndEveryCompile()
    {
        var e = Engine();
        e.ConsultString(Tables);
        Assert.True(e.Query("fact_table(_, _), with_a_rule(_).").Success);
        var rule = e.DynamicPredicateCache[Fid("with_a_rule", 1)];
        int derivation = e._derivationGen;
        long layouts = e.PersistentInvalidations;

        Assert.True(e.Query("jit_compile(off).").Success);
        Assert.True(e.Query("findall(K, fact_table(K, _), [a, b, c]), with_a_rule(11).").Success);

        Assert.True(e.PersistentInvalidations > layouts, "the tier change did not lay the program out again");
        Assert.Equal(derivation, e._derivationGen);
        Assert.Same(rule, e.DynamicPredicateCache[Fid("with_a_rule", 1)]);
    }
}
