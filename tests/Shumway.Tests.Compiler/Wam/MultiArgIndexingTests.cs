using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>
/// Chunk 67 — bytecode-level pinning for multi-arg sequential fallback
/// indexing. Each test compiles a predicate whose first argument is
/// non-discriminating (all clauses have the same shape or var at A1)
/// but a later argument discriminates, and verifies the new
/// <c>switch_on_arg</c> / <c>switch_on_atom_arg</c> /
/// <c>switch_on_integer_arg</c> / <c>switch_on_structure_arg</c>
/// opcodes appear in the emitted bytecode.
///
/// <para>End-to-end correctness lives in
/// <see cref="Shumway.Tests.Embedding"/>; these tests guard against a
/// regression where the multi-arg layer is silently dropped (the
/// predicate would still answer correctly via the fall-through chain,
/// but lose the indexing benefit).</para>
/// </summary>
public class MultiArgIndexingTests
{
    private static CompiledPredicate CompilePredicate(string source)
    {
        var clauses = new ClauseReader(source).ReadAll().ToList();
        Assert.True(clauses.Count >= 1);
        return new PredicateCompiler().Compile(clauses);
    }

    private static bool ContainsOpcode(byte[] code, Opcode target)
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

    private static int CountOpcode(byte[] code, Opcode target)
    {
        int count = 0;
        int pc = 0;
        while (pc < code.Length)
        {
            if ((Opcode)code[pc] == target) count++;
            var info = OpcodeTable.Get(code[pc]);
            if (info.Size == 0) break;
            pc += info.Size;
        }
        return count;
    }

    [Fact]
    public void Arg1OnlyIndexable_EmitsSwitchOnArg1AndAtomArg()
    {
        // All clauses have a var first arg. Arg 1 discriminates by atom.
        var pred = CompilePredicate(
            "foo(_, a) :- one.\n" +
            "foo(_, b) :- two.\n" +
            "foo(_, c) :- three.\n");

        // Arg 0 is all var → no switch_on_term (no discriminating arg 0).
        Assert.False(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnTerm),
            "Arg 0 has no concrete values; switch_on_term shouldn't be emitted.");
        // Arg 1 is indexable → switch_on_arg + switch_on_atom_arg.
        Assert.True(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnArg),
            "Arg 1 is indexable; switch_on_arg should be emitted.");
        Assert.True(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnAtomArg),
            "Arg 1 has atom clauses; switch_on_atom_arg should be emitted.");
    }

    [Fact]
    public void BothArgsIndexable_EmitsBothSwitchOnTermAndSwitchOnArg()
    {
        // Arg 0 discriminates (a/b) AND arg 1 discriminates (x/y).
        // Both levels are emitted: switch_on_term for arg 0, then
        // switch_on_arg for arg 1.
        var pred = CompilePredicate(
            "foo(a, x) :- one.\n" +
            "foo(a, y) :- two.\n" +
            "foo(b, x) :- three.\n" +
            "foo(b, y) :- four.\n");

        Assert.True(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnTerm));
        Assert.True(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnArg));
        // One switch_on_atom (arg 0) and one switch_on_atom_arg (arg 1).
        Assert.Equal(1, CountOpcode(pred.Bytecode, Opcode.SwitchOnAtom));
        Assert.Equal(1, CountOpcode(pred.Bytecode, Opcode.SwitchOnAtomArg));
    }

    [Fact]
    public void Arg2Indexable_EmitsSwitchOnArg2()
    {
        // Args 0 and 1 are var across clauses; arg 2 discriminates.
        var pred = CompilePredicate(
            "foo(_, _, alpha) :- one.\n" +
            "foo(_, _, beta) :- two.\n");

        Assert.False(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnTerm));
        // The switch_on_arg's first operand (arg_idx) is 2.
        Assert.True(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnArg));
        int pc = FindOpcode(pred.Bytecode, Opcode.SwitchOnArg);
        int argIdx = BytecodeIO.ReadInt32(pred.Bytecode, pc + 1);
        Assert.Equal(2, argIdx);
    }

    [Fact]
    public void Arg1IntegerIndex_EmitsSwitchOnIntegerArg()
    {
        var pred = CompilePredicate(
            "foo(_, 1) :- one.\n" +
            "foo(_, 2) :- two.\n" +
            "foo(_, 3) :- three.\n");
        Assert.True(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnIntegerArg));
    }

    [Fact]
    public void Arg1StructureIndex_EmitsSwitchOnStructureArg()
    {
        var pred = CompilePredicate(
            "foo(_, p(X)) :- one.\n" +
            "foo(_, q(X)) :- two.\n");
        Assert.True(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnStructureArg));
    }

    [Fact]
    public void NoArgIndexable_FallsBackToTryMeChain()
    {
        // All clauses have var arg 0 and var arg 1 → no indexing.
        var pred = CompilePredicate(
            "foo(_, _) :- one.\n" +
            "foo(_, _) :- two.\n");

        Assert.False(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnTerm));
        Assert.False(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnArg));
        Assert.True(ContainsOpcode(pred.Bytecode, Opcode.TryMeElse));
    }

    [Fact]
    public void Arg0OnlyIndexed_StillUsesSwitchOnTermOnly()
    {
        // Existing arg-0-only case: still emits switch_on_term, no
        // switch_on_arg. This is the chunk-18 path; the regression test
        // makes sure the chunk-67 refactor didn't change it.
        var pred = CompilePredicate(
            "foo(a, _) :- one.\n" +
            "foo(b, _) :- two.\n" +
            "foo(c, _) :- three.\n");

        Assert.True(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnTerm));
        Assert.False(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnArg));
        Assert.True(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnAtom));
        Assert.False(ContainsOpcode(pred.Bytecode, Opcode.SwitchOnAtomArg));
    }

    private static int FindOpcode(byte[] code, Opcode target)
    {
        int pc = 0;
        while (pc < code.Length)
        {
            if ((Opcode)code[pc] == target) return pc;
            var info = OpcodeTable.Get(code[pc]);
            if (info.Size == 0) break;
            pc += info.Size;
        }
        return -1;
    }
}
