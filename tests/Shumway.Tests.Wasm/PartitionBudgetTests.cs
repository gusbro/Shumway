using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The partition budget cuts a module into functions so the
/// browser's JIT never sees one over its cliff. It is a safety valve, and
/// this says where the valve actually sits.
///
/// <para>Measured over the 823 compilable predicates of the prelude plus
/// clpfd: compiled ONE a module, not a single one is cut (the largest,
/// $prelude$$must_be_ok/2 at 2170 bytecode bytes, still fits in one
/// function). The whole program as one group takes 7. At a grain of 16 or
/// 64 predicates a module, no module is ever cut.</para>
///
/// <para>So the valve is load-bearing for the batch mode, which compiles the
/// linked program as one module, and idle for every finer grain -- but it
/// stays: a generated fact table is one predicate that can cross the budget
/// alone, and that case has no other guard.</para></summary>
public sealed class PartitionBudgetTests(ITestOutputHelper o)
{
    /// <summary>Partitions of a compiled module. Its function section holds
    /// k+3 (run, k partitions, the fail/proceed resolver, the unifier).</summary>
    private static int PartitionsOf(byte[] module)
    {
        int at = 8;   // magic + version
        while (at < module.Length)
        {
            int id = module[at++];
            long size = ReadLeb(module, ref at);
            if (id == 3)
            {
                int p = at;
                // run, the fail/proceed resolver, the unifier, the
                // comparator, the expression evaluator and the two walks
                // (ground, acyclic) are not partitions.
                return (int)ReadLeb(module, ref p) - 7;
            }
            at += (int)size;
        }
        throw new Xunit.Sdk.XunitException("no function section");
    }

    private static long ReadLeb(byte[] b, ref int at)
    {
        long v = 0; int shift = 0;
        while (true)
        {
            byte x = b[at++];
            v |= (long)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return v;
            shift += 7;
        }
    }

    [Fact]
    public void TheBudgetCutsAGroupAndNeverASinglePredicate()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).");
        e.Query("true.");
        var env = new EngineWasmCompileEnv();

        var members = new List<WasmGroupMember>();
        WasmGroupMember? biggest = null;
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(e))
        {
            var m = new WasmGroupMember(pred, addr, null);
            try { WasmPredicateCompiler.CompileGroup(new[] { m }, env); }
            catch (WasmCompileException) { continue; }
            members.Add(m);
            if (biggest is null
                || pred.Bytecode.Length > biggest.Predicate.Bytecode.Length)
                biggest = m;
        }
        Assert.True(members.Count > 500, $"only {members.Count} compilable");
        Assert.NotNull(biggest);

        // The predicate most likely to be cut, alone, is not cut.
        int alone = PartitionsOf(
            WasmPredicateCompiler.CompileGroup(new[] { biggest! }, env).Module);
        var (aid, ar) = Shumway.Core.FunctorTable.Lookup(biggest!.Predicate.FunctorId);
        o.WriteLine($"largest predicate {Shumway.Core.AtomTable.GetById(aid)?.Name}/{ar}"
            + $" ({biggest.Predicate.Bytecode.Length} bytecode bytes): {alone} partition(s)");
        Assert.Equal(1, alone);

        // The whole program as one module is cut, which is the batch mode.
        int group = PartitionsOf(
            WasmPredicateCompiler.CompileGroup(members, env).Module);
        o.WriteLine($"the whole program as one group of {members.Count}: {group} partitions");
        Assert.True(group > 1,
            "the whole program fit in one function: either the corpus shrank or "
            + "the budget stopped cutting, and the browser's JIT cliff is the "
            + "reason the cut exists");
    }
}
