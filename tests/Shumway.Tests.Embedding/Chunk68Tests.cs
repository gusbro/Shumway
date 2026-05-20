using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 68 — indexing for dynamic predicates (Phase 2). The WAM
/// compiler has always indexed dynamic clauses the same way as static
/// ones (chunk 18 didn't gate on dynamic-ness); the gap ADR-007
/// flagged was the <em>cost</em>: every query re-rewrote and
/// re-compiled every dynamic predicate's clause set, so a 1000-fact
/// dynamic predicate paid an O(N) compile cost per call.
///
/// <para>Chunk 68 adds a per-engine cache of compiled dynamic
/// predicates that's populated lazily after the first query and
/// invalidated on every <c>assertz</c> / <c>asserta</c> /
/// <c>retract</c> / <c>abolish</c> that touches the functor. The
/// indexing itself was already there; the cache makes its benefit
/// observable across queries.</para>
/// </summary>
public class Chunk68Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void DynamicPredicate_CompilesWithIndexing()
    {
        // Build a dynamic predicate with discriminating arg 0 atoms.
        // Chunk 75 (JIT indexing) defers the switch tables until the
        // predicate is hot, so drop the threshold to 1 — the first
        // call then makes it hot and the next query recompiles indexed
        // with switch_on_term + switch_on_atom, the same shape a
        // static indexed predicate gets.
        var engine = new PrologEngine();
        engine.JitIndexing.Threshold = 1;
        engine.ConsultString(":- dynamic color/1.");
        foreach (var c in new[] { "red", "green", "blue", "yellow", "purple" })
            engine.Query($"assertz(color({c})).");
        // First query: predicate crosses the (lowered) threshold.
        Assert.True(engine.Query("color(red).").Success);
        // Second query: recompiled indexed now that it's hot.
        Assert.True(engine.Query("color(green).").Success);

        Assert.True(engine.DynamicPredicateCache.TryGetValue(Fid("color", 1), out var cached));
        Assert.NotNull(cached);
        Assert.Contains(cached!.Bytecode, b => (Opcode)b == Opcode.SwitchOnTerm);
        Assert.Contains(cached.Bytecode, b => (Opcode)b == Opcode.SwitchOnAtom);
    }

    [Fact]
    public void Cache_PopulatesAfterFirstQuery()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic d/1.\nd(a).\nd(b).\nd(c).");
        // No queries yet → cache is empty.
        Assert.False(engine.DynamicPredicateCache.ContainsKey(Fid("d", 1)));
        // First query populates.
        engine.Query("d(a).");
        Assert.True(engine.DynamicPredicateCache.ContainsKey(Fid("d", 1)));
    }

    [Fact]
    public void Cache_InvalidatesOnAssertz()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic d/1.\nd(a).");
        engine.Query("d(a).");
        Assert.True(engine.DynamicPredicateCache.ContainsKey(Fid("d", 1)));
        engine.Query("assertz(d(b)).");
        Assert.False(engine.DynamicPredicateCache.ContainsKey(Fid("d", 1)));
        // Next query picks up the new clause and re-caches.
        Assert.True(engine.Query("d(b).").Success);
        Assert.True(engine.DynamicPredicateCache.ContainsKey(Fid("d", 1)));
    }

    [Fact]
    public void Cache_InvalidatesOnAsserta()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic d/1.\nd(a).");
        engine.Query("d(a).");
        Assert.True(engine.DynamicPredicateCache.ContainsKey(Fid("d", 1)));
        engine.Query("asserta(d(z)).");
        Assert.False(engine.DynamicPredicateCache.ContainsKey(Fid("d", 1)));
    }

    [Fact]
    public void Cache_InvalidatesOnRetract()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic d/1.\nd(a).\nd(b).");
        engine.Query("d(a).");
        Assert.True(engine.DynamicPredicateCache.ContainsKey(Fid("d", 1)));
        engine.Query("retract(d(a)).");
        Assert.False(engine.DynamicPredicateCache.ContainsKey(Fid("d", 1)));
        // d(a) is gone; d(b) survives.
        Assert.False(engine.Query("d(a).").Success);
        Assert.True(engine.Query("d(b).").Success);
    }

    [Fact]
    public void Cache_InvalidatesOnAbolish()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic d/1.\nd(a).\nd(b).\nd(c).");
        engine.Query("d(a).");
        Assert.True(engine.DynamicPredicateCache.ContainsKey(Fid("d", 1)));
        engine.Query("abolish(d/1).");
        Assert.False(engine.DynamicPredicateCache.ContainsKey(Fid("d", 1)));
    }

    [Fact]
    public void AssertRetractCycle_StaysCorrect()
    {
        // Long modify-query cycle: each modification must invalidate
        // the cache so subsequent queries see the current clause set.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic counter/1.\ncounter(0).");
        for (int i = 1; i <= 5; i++)
        {
            engine.Query($"retract(counter({i - 1})).");
            engine.Query($"assertz(counter({i})).");
            Assert.True(engine.Query($"counter({i}).").Success, $"counter({i}) should succeed");
            Assert.False(engine.Query($"counter({i - 1}).").Success, $"counter({i - 1}) should fail");
        }
    }

    [Fact]
    public void MultiArgIndexing_AppliesToDynamic()
    {
        // The chunk-67 multi-arg fallback works on dynamic predicates too,
        // because dynamic compile goes through the same PredicateCompiler.
        // Chunk 75 (JIT indexing) defers the switch tables until hot —
        // threshold 1 makes the first call promote it.
        var engine = new PrologEngine();
        engine.JitIndexing.Threshold = 1;
        engine.ConsultString(":- dynamic shape/2.");
        engine.Query("assertz(shape(circle, area)).");
        engine.Query("assertz(shape(square, area)).");
        engine.Query("assertz(shape(circle, perimeter)).");
        engine.Query("assertz(shape(triangle, area)).");
        engine.Query("shape(circle, area).");   // crosses threshold
        engine.Query("shape(circle, area).");   // recompiled indexed
        // Cached bytecode should contain both switch_on_term (arg 0)
        // and switch_on_arg (arg 1 fallback).
        Assert.True(engine.DynamicPredicateCache.TryGetValue(Fid("shape", 2), out var cached));
        bool hasSwitchOnTerm = false, hasSwitchOnArg = false;
        int pc = 0;
        while (pc < cached!.Bytecode.Length)
        {
            var op = (Opcode)cached.Bytecode[pc];
            if (op == Opcode.SwitchOnTerm) hasSwitchOnTerm = true;
            if (op == Opcode.SwitchOnArg) hasSwitchOnArg = true;
            var info = OpcodeTable.Get((byte)op);
            if (info.Size == 0) break;
            pc += info.Size;
        }
        Assert.True(hasSwitchOnTerm, "Arg 0 should be indexed via switch_on_term");
        Assert.True(hasSwitchOnArg, "Arg 1 should be indexed via switch_on_arg");
        Assert.Single(engine.QueryAll("shape(_, perimeter)."));
    }

    [Fact]
    public void RebuildPicksUpNewClause()
    {
        // After assertz, the next query must compile the new clause set
        // and the result must include the new clause.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic fact/1.");
        engine.Query("assertz(fact(a)).");
        engine.Query("fact(a).");
        Assert.True(engine.DynamicPredicateCache.ContainsKey(Fid("fact", 1)));
        engine.Query("assertz(fact(b)).");
        // Cache was invalidated; next query rebuilds.
        Assert.True(engine.Query("fact(b).").Success);
        Assert.True(engine.DynamicPredicateCache.ContainsKey(Fid("fact", 1)));
    }

    [Fact]
    public void LargeFactSet_IsCachedAndCorrect()
    {
        // 100-fact dynamic predicate. The cache eliminates repeat
        // compilation; correctness is the test bar.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic big/1.");
        for (int i = 0; i < 100; i++)
            engine.Query($"assertz(big({i})).");
        // Prime the cache.
        engine.Query("big(50).");
        Assert.True(engine.DynamicPredicateCache.ContainsKey(Fid("big", 1)));
        // Spot-check correctness across the range.
        for (int i = 0; i < 100; i += 7)
            Assert.True(engine.Query($"big({i}).").Success, $"big({i}) failed");
        Assert.False(engine.Query("big(999).").Success);
        // After all those reads the cache should still be populated
        // (none of the queries mutated the predicate).
        Assert.True(engine.DynamicPredicateCache.ContainsKey(Fid("big", 1)));
    }
}
