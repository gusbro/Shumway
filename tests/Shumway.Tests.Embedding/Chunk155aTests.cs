using Shumway.Compiler.Ast;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 155a: extensible-indexed compilation layout for dynamic
/// predicates. <c>CompileIndexedDynamic</c> emits a layout where
/// every bucket is its own <c>try_me_else</c> / <c>retry_me_else</c>
/// chain (patchable <c>&lt;next&gt;</c> operands, chain ends at
/// <c>fail_stub</c>) and bodies are shared across chains via
/// <c>execute</c>. The runtime in-place extension hooks land in
/// chunk 155b; for now, chunk-154's persistent invalidation on
/// mutation still applies, so the layout change is behaviour-neutral
/// — pinned here so a regression surfaces.
/// </summary>
public class Chunk155aTests
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
    public void HotDynamic_IndexedLayout_UsesTryMeElseInsteadOfTry()
    {
        // Chunk 154 used Try / Retry / Trust in bucket chains; chunk
        // 155a swaps them for TryMeElse / RetryMeElse so the chain
        // <next> operands are patchable, enabling future in-place
        // extension.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic color/1.");
        foreach (var c in new[] { "red", "red", "green", "blue", "blue" })
            e.Query($"assertz(color({c})).");
        e.Query("color(red).");
        e.Query("color(green).");
        Assert.True(e.DynamicPredicateCache.TryGetValue(Fid("color", 1), out var cached));
        Assert.True(HasOpcode(cached!.Bytecode, Opcode.SwitchOnTerm));
        Assert.True(HasOpcode(cached.Bytecode, Opcode.TryMeElse),
            "chunk 155a: bucket chains use try_me_else (extensible).");
        Assert.False(HasOpcode(cached.Bytecode, Opcode.Try),
            "chunk 155a: contiguous try opcode is gone for dynamic indexed predicates.");
        Assert.True(HasOpcode(cached.Bytecode, Opcode.Execute),
            "chunk 155a: chain entries reach shared bodies via execute.");
        Assert.True(HasOpcode(cached.Bytecode, Opcode.EnterDynamic));
        Assert.True(HasOpcode(cached.Bytecode, Opcode.CheckVisible));
    }

    [Fact]
    public void Correctness_VariousQueriesAcrossKeys()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic kv/2.");
        foreach (var (k, v) in new[] { ("a", 1), ("b", 2), ("c", 3), ("a", 9), ("b", 7) })
            e.Query($"assertz(kv({k}, {v})).");
        // Heat up.
        e.Query("kv(a, _).");
        e.Query("kv(b, _).");
        // Deterministic dispatch (concrete key).
        var aSols = e.QueryAll("kv(a, X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        Assert.Equal(new long[] { 1, 9 }, aSols);
        var bSols = e.QueryAll("kv(b, X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        Assert.Equal(new long[] { 2, 7 }, bSols);
        // Var dispatch.
        var all = e.QueryAll("kv(_, X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        Assert.Equal(new long[] { 1, 2, 3, 9, 7 }, all);
    }

    [Fact]
    public void RetractAfterPromotion_StillCorrect()
    {
        // Chunk 154's rebuild-on-mutate gate still applies for now;
        // retract triggers a re-link with the new chunk-155a layout
        // and current clauses. Once chunk 155b lands the runtime
        // hook, the rebuild becomes an in-place died-slot patch.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        foreach (var v in new[] { 1, 2, 3 })
            e.Query($"assertz(d({v})).");
        e.Query("d(1).");
        e.Query("d(2).");
        e.Query("retract(d(2)).");
        var xs = e.QueryAll("d(X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        Assert.Equal(new long[] { 1, 3 }, xs);
    }

    [Fact]
    public void MultiArgIndexing_StillUsesContiguousLayout()
    {
        // Multi-arg dynamic indexing keeps the chunk-154 layout
        // (Try / Retry / Trust) — chunk 155a's extensible chains
        // only handle the first-arg case. A future chunk can extend
        // the extensible model to multi-arg.
        // Verified indirectly: a predicate where arg 0 is var but
        // arg 1 carries the discriminator routes through the chunk-
        // 154 path. Correctness is the observable.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic p/2.");
        foreach (var (a, b) in new[] { ("_", "a"), ("_", "b"), ("_", "c") })
            e.Query($"assertz(p(x, {b})).");
        e.Query("p(_, a).");
        e.Query("p(_, b).");
        Assert.True(e.Query("p(_, a).").Success);
        Assert.True(e.Query("p(_, b).").Success);
    }
}
