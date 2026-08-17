using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>
/// Chunk 122 (Phase 8, ADR-015 chunk C step 3): the compiler emits
/// <c>enter_dynamic</c> at every dynamic predicate's entry and
/// <c>check_visible</c> in front of every clause's body. Step-3 values
/// are the always-visible sentinels (born=0, died=long.MaxValue); step 4
/// will wire real born/died.
/// </summary>
public class Chunk122Tests
{
    private static int Functor(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static CompiledModule Compile(string source, params (string Name, int Arity)[] dynamic)
    {
        var clauses = new ClauseReader(source).ReadAll().ToList();
        var dyn = new HashSet<int>(dynamic.Select(d => Functor(d.Name, d.Arity)));
        return new ModuleCompiler().Compile(
            clauses, cache: null, unindexedFunctors: dyn, pools: null,
            dynamicFunctors: dyn);
    }

    private static bool ContainsOpcode(byte[] bytecode, Opcode op)
    {
        // Walk by opcode sizes to avoid mis-matching operand bytes.
        int pc = 0;
        while (pc < bytecode.Length)
        {
            var info = OpcodeTable.Get(bytecode[pc]);
            if (info.Op == op) return true;
            pc += info.Size;
        }
        return false;
    }

    [Fact]
    public void SingleClauseDynamic_GetsEnterDynamicAndCheckVisible()
    {
        var module = Compile("d(1).\n", ("d", 1));
        var pred = module.Predicates.Single(p => p.FunctorId == Functor("d", 1));

        Assert.Equal((byte)Opcode.EnterDynamic, pred.Bytecode[0]);
        Assert.Equal((byte)Opcode.CheckVisible, pred.Bytecode[1]);

        // The always-visible sentinels: born=0, died=long.MaxValue.
        long born = BytecodeIO.ReadInt64(pred.Bytecode, 2);
        long died = BytecodeIO.ReadInt64(pred.Bytecode, 10);
        Assert.Equal(0L, born);
        Assert.Equal(long.MaxValue, died);
    }

    [Fact]
    public void MultiClauseDynamic_GetsEnterDynamicOnceAndCheckVisiblePerClause()
    {
        var module = Compile("d(1).\nd(2).\nd(3).\n", ("d", 1));
        var pred = module.Predicates.Single(p => p.FunctorId == Functor("d", 1));

        // One enter_dynamic at the entry — the first byte.
        Assert.Equal((byte)Opcode.EnterDynamic, pred.Bytecode[0]);

        // Three check_visible — one per clause.
        int checks = 0;
        int pc = 0;
        while (pc < pred.Bytecode.Length)
        {
            var info = OpcodeTable.Get(pred.Bytecode[pc]);
            if (info.Op == Opcode.CheckVisible) checks++;
            pc += info.Size;
        }
        Assert.Equal(3, checks);
    }

    [Fact]
    public void StaticPredicate_HasNoEnterDynamicOrCheckVisible()
    {
        var module = Compile("""
            s(1).
            s(2).
            """);      // no dynamic marker
        var pred = module.Predicates.Single(p => p.FunctorId == Functor("s", 1));

        Assert.False(ContainsOpcode(pred.Bytecode, Opcode.EnterDynamic));
        Assert.False(ContainsOpcode(pred.Bytecode, Opcode.CheckVisible));
    }

    [Fact]
    public void DynamicChainBackPointers_AreCorrectWithCheckVisibleSpace()
    {
        // Sanity that adding 17 bytes per clause didn't break the
        // try_me_else / retry_me_else target arithmetic: every dispatch
        // BP must land on another try/retry/trust opcode.
        var module = Compile("d(1).\nd(2).\nd(3).\nd(4).\n", ("d", 1));
        var pred = module.Predicates.Single(p => p.FunctorId == Functor("d", 1));

        foreach (int site in pred.DispatchSites)
        {
            int target = BytecodeIO.ReadInt32(pred.Bytecode, site);
            byte targetOp = pred.Bytecode[target];
            Assert.True(
                targetOp == (byte)Opcode.TryMeElse
                || targetOp == (byte)Opcode.RetryMeElse
                || targetOp == (byte)Opcode.TrustMe,
                $"Dispatch BP at site {site} -> {target} landed on opcode 0x{targetOp:X2}, " +
                "expected a try/retry/trust.");
        }
    }
}
