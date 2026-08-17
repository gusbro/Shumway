using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 75 — JIT indexing (Phase 3). A dynamic predicate compiles to
/// a plain <c>try_me_else</c> chain — cheap to build, O(N) to dispatch
/// — until its runtime call count crosses
/// <see cref="JitIndexProfile.Threshold"/>. The first query after that
/// recompiles it with full multi-arg indexing (switch tables, O(1)
/// dispatch). A dynamic predicate that's rarely called, or churning
/// under heavy assertz / retract, never pays the switch-table build
/// cost.
///
/// <para>These tests pin both halves of the contract: the cold form
/// is unindexed, the hot form is indexed, and the observable answers
/// are identical either way. Static predicates are unaffected — they
/// always compile indexed.</para>
/// </summary>
public class Chunk75Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static bool HasOpcode(byte[] code, Opcode target)
    {
        int pc = 0;
        while (pc < code.Length)
        {
            if ((Opcode)code[pc] == target) return true;
            var info = OpcodeTable.Get(code[pc]);
            if (info.Size == 0) break;
            pc += info.Size;
        }
        return false;
    }

    [Fact]
    public void ColdDynamicPredicate_CompilesUnindexed()
    {
        // Default threshold (16) — a predicate queried once stays cold.
        // Its cached compile is a try_me_else chain: no switch_on_term.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic color/1.");
        foreach (var c in new[] { "red", "green", "blue" })
            engine.Query($"assertz(color({c})).");
        engine.Query("color(red).");

        Assert.True(engine.DynamicPredicateCache.TryGetValue(Fid("color", 1), out var cached));
        Assert.False(HasOpcode(cached!.Bytecode, Opcode.SwitchOnTerm),
            "a cold dynamic predicate should compile unindexed.");
        Assert.True(HasOpcode(cached.Bytecode, Opcode.TryMeElse),
            "the cold form is a plain try_me_else chain.");
    }

    [Fact]
    public void HotDynamicPredicate_RecompilesIndexed()
    {
        // Threshold 1 — the first call makes the predicate hot, the
        // next query recompiles it indexed.
        var engine = new PrologEngine();
        engine.JitIndexing.Threshold = 1;
        engine.ConsultString(":- dynamic color/1.");
        foreach (var c in new[] { "red", "green", "blue" })
            engine.Query($"assertz(color({c})).");
        engine.Query("color(red).");    // crosses threshold (count → 1)
        engine.Query("color(green).");  // setup recompiles indexed

        Assert.True(engine.DynamicPredicateCache.TryGetValue(Fid("color", 1), out var cached));
        Assert.True(HasOpcode(cached!.Bytecode, Opcode.SwitchOnTerm),
            "a hot dynamic predicate should be recompiled indexed.");
    }

    [Fact]
    public void Correctness_IdenticalColdAndHot()
    {
        // The answers must not depend on the indexing level.
        var engine = new PrologEngine();
        engine.JitIndexing.Threshold = 3;
        engine.ConsultString(":- dynamic kv/2.");
        foreach (var (k, v) in new[] { ("a", 1), ("b", 2), ("c", 3), ("a", 9) })
            engine.Query($"assertz(kv({k}, {v})).");

        // Cold queries.
        Assert.Equal(2, engine.QueryAll("kv(a, _).").Count());
        Assert.True(engine.Query("kv(b, 2).").Success);
        Assert.False(engine.Query("kv(z, _).").Success);

        // Drive it hot, then re-check — same answers.
        for (int i = 0; i < 5; i++) engine.Query("kv(c, 3).");
        Assert.Equal(2, engine.QueryAll("kv(a, _).").Count());
        Assert.True(engine.Query("kv(b, 2).").Success);
        Assert.False(engine.Query("kv(z, _).").Success);
    }

    [Fact]
    public void CallCount_IsTracked()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- dynamic ping/0.
            ping.
            """);
        Assert.Equal(0, engine.JitIndexing.CallCountFor(Fid("ping", 0)));
        engine.Query("ping.");
        engine.Query("ping.");
        engine.Query("ping.");
        Assert.Equal(3, engine.JitIndexing.CallCountFor(Fid("ping", 0)));
    }

    [Fact]
    public void Threshold_GovernsTheTransition()
    {
        var engine = new PrologEngine();
        engine.JitIndexing.Threshold = 5;
        engine.ConsultString(":- dynamic d/1.");
        foreach (var c in new[] { "a", "b", "c" })
            engine.Query($"assertz(d({c})).");

        // Four calls — still below threshold 5, still cold.
        for (int i = 0; i < 4; i++) engine.Query("d(a).");
        Assert.False(engine.JitIndexing.IsHot(Fid("d", 1)));
        engine.DynamicPredicateCache.TryGetValue(Fid("d", 1), out var coldCached);
        Assert.False(HasOpcode(coldCached!.Bytecode, Opcode.SwitchOnTerm));

        // Fifth call crosses it; the next query recompiles indexed.
        engine.Query("d(a).");
        Assert.True(engine.JitIndexing.IsHot(Fid("d", 1)));
        engine.Query("d(a).");
        engine.DynamicPredicateCache.TryGetValue(Fid("d", 1), out var hotCached);
        Assert.True(HasOpcode(hotCached!.Bytecode, Opcode.SwitchOnTerm));
    }

    [Fact]
    public void StaticPredicate_AlwaysCompilesIndexed()
    {
        // JIT indexing only defers *dynamic* predicates. A static
        // multi-clause predicate with discriminating clauses compiles
        // indexed on the first (and only) compile — verified directly
        // through PredicateCompiler, whose enableIndexing defaults to
        // true.
        var clauses = new ClauseReader(
            "shape(circle). shape(square). shape(triangle).").ReadAll().ToList();
        var pred = new PredicateCompiler().Compile(clauses);
        Assert.True(HasOpcode(pred.Bytecode, Opcode.SwitchOnTerm));
    }

    [Fact]
    public void Compiler_EnableIndexingFalse_EmitsChain()
    {
        // The compiler knob the JIT layer drives: enableIndexing=false
        // forces the try_me_else chain even for an indexable predicate.
        var clauses = new ClauseReader(
            "shape(circle). shape(square). shape(triangle).").ReadAll().ToList();
        var pred = new PredicateCompiler().Compile(
            clauses,
            new LiteralPool<string>(),
            new LiteralPool<double>(),
            new LiteralPool<System.Numerics.BigInteger>(),
            enableIndexing: false);
        Assert.False(HasOpcode(pred.Bytecode, Opcode.SwitchOnTerm));
        Assert.True(HasOpcode(pred.Bytecode, Opcode.TryMeElse));
    }

    [Fact]
    public void ChurningDynamicPredicate_StaysUnindexedWhileCold()
    {
        // assertz between queries invalidates the cache (chunk 68).
        // While the predicate is still cold each rebuild is the cheap
        // unindexed compile — no switch tables built on the churn.
        var engine = new PrologEngine();
        engine.JitIndexing.Threshold = 100;   // never hot in this test
        engine.ConsultString(":- dynamic log/1.");
        for (int i = 0; i < 10; i++)
        {
            engine.Query($"assertz(log(e{i})).");
            engine.Query($"log(e{i}).");
        }
        Assert.True(engine.DynamicPredicateCache.TryGetValue(Fid("log", 1), out var cached));
        Assert.False(HasOpcode(cached!.Bytecode, Opcode.SwitchOnTerm),
            "a churning, never-hot dynamic predicate stays unindexed.");
        // Still correct.
        Assert.True(engine.Query("log(e7).").Success);
        Assert.False(engine.Query("log(nope).").Success);
    }

    [Fact]
    public void HotThenModified_RecompilesAtCurrentLevel()
    {
        // A predicate that's gone hot, then gets a new clause via
        // assertz: the cache invalidates, and since it's still hot the
        // recompile stays indexed.
        var engine = new PrologEngine();
        engine.JitIndexing.Threshold = 1;
        engine.ConsultString(":- dynamic d/1.");
        engine.Query("assertz(d(a)).");
        engine.Query("assertz(d(b)).");
        engine.Query("d(a).");          // count → 1, hot
        engine.Query("d(a).");          // recompiled indexed
        engine.Query("assertz(d(c)).");  // invalidates the cache
        engine.Query("d(c).");          // recompiled — still hot → indexed

        Assert.True(engine.DynamicPredicateCache.TryGetValue(Fid("d", 1), out var cached));
        Assert.True(HasOpcode(cached!.Bytecode, Opcode.SwitchOnTerm));
        Assert.True(engine.Query("d(c).").Success);
    }

    [Fact]
    public void MultiArgIndexing_KicksInWhenHot()
    {
        // The chunk-67 multi-arg fallback (switch_on_arg) shows up once
        // a multi-arg dynamic predicate goes hot.
        var engine = new PrologEngine();
        engine.JitIndexing.Threshold = 1;
        engine.ConsultString(":- dynamic pair/2.");
        engine.Query("assertz(pair(x, 1)).");
        engine.Query("assertz(pair(y, 2)).");
        engine.Query("pair(x, 1).");
        engine.Query("pair(x, 1).");

        Assert.True(engine.DynamicPredicateCache.TryGetValue(Fid("pair", 2), out var cached));
        Assert.True(HasOpcode(cached!.Bytecode, Opcode.SwitchOnTerm));
    }

    [Fact]
    public void UndeclaredThreshold_DefaultKeepsRareCallsCold()
    {
        // With the default threshold, a one-shot dynamic predicate
        // never indexes — the common "consult, query once" shape pays
        // no switch-table build cost.
        var engine = new PrologEngine();
        // `rare` is just an arbitrary data predicate here — not once/1,
        // which is now a library control predicate in the prelude.
        engine.ConsultString("""
            :- dynamic rare/1.
            rare(a).
            rare(b).
            """);
        engine.Query("rare(a).");
        Assert.False(engine.JitIndexing.IsHot(Fid("rare", 1)));
        Assert.True(engine.DynamicPredicateCache.TryGetValue(Fid("rare", 1), out var cached));
        Assert.False(HasOpcode(cached!.Bytecode, Opcode.SwitchOnTerm));
    }
}
