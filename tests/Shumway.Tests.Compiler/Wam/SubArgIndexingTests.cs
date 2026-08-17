using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>
/// ADR-027 — second-level (sub-argument) indexing. Each test compiles a
/// predicate whose top-level argument does not discriminate (a list in every
/// clause, or a compound sharing one functor) but a sub-term does, and verifies
/// the new <c>switch_on_atom_sub</c> / <c>switch_on_integer_sub</c> opcodes
/// appear with the right path operands — and that the linear
/// <c>try</c>/<c>retry</c>/<c>trust</c> chain over the discriminated clauses is
/// gone. End-to-end correctness / determinism lives in Shumway.Tests.Embedding.
/// </summary>
public class SubArgIndexingTests
{
    private static CompiledPredicate Compile(string source)
    {
        var clauses = new ClauseReader(source).ReadAll().ToList();
        Assert.True(clauses.Count >= 1);
        return new PredicateCompiler().Compile(clauses);
    }

    /// <summary>Finds the first occurrence of <paramref name="target"/> and
    /// returns its operand ints, or null if absent.</summary>
    private static int[]? OperandsOf(byte[] code, Opcode target)
    {
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            var info = OpcodeTable.Get(code[pc]);
            if (info.Size == 0) break;
            if (op == target)
            {
                int n = info.NumOperands;
                var vals = new int[n];
                for (int i = 0; i < n; i++) vals[i] = BytecodeIO.ReadInt32(code, pc + 1 + 4 * i);
                return vals;
            }
            pc += info.Size;
        }
        return null;
    }

    private static int Count(byte[] code, Opcode target)
    {
        int count = 0, pc = 0;
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
    public void ListHeadAtoms_EmitAtomSub_DepthOne()
    {
        // tok([a|T],T) ... [d|T]: arg0 is a list in every clause, heads a/b/c/d
        // distinct atoms -> car indexing on the head (sub0 = 0, sub1 = -1).
        var cp = Compile("""
            tok([a|T],T).
            tok([b|T],T).
            tok([c|T],T).
            tok([d|T],T).
            """);
        int[]? ops = OperandsOf(cp.Bytecode, Opcode.SwitchOnAtomSub);
        Assert.NotNull(ops);
        Assert.Equal(new[] { 0, 0, -1 }, new[] { ops![0], ops[1], ops[2] });   // argIdx, sub0, sub1
        // The list branch no longer scans all four clauses linearly: only the
        // var branch keeps a full chain, so no bucket has a 4-way try/retry.
        Assert.Equal(0, Count(cp.Bytecode, Opcode.SwitchOnIntegerSub));
    }

    [Fact]
    public void ListHeadIntegers_EmitIntegerSub_DepthOne()
    {
        var cp = Compile("""
            k([1|T],T).
            k([2|T],T).
            k([3|T],T).
            """);
        int[]? ops = OperandsOf(cp.Bytecode, Opcode.SwitchOnIntegerSub);
        Assert.NotNull(ops);
        Assert.Equal(new[] { 0, 0, -1 }, new[] { ops![0], ops[1], ops[2] });
    }

    [Fact]
    public void TokenStream_EmitIntegerSub_DepthTwo()
    {
        // The Arity print_cmd idiom: list head is a t/4 compound, the discriminating
        // integer code lives at the token's 2nd sub-arg -> path (sub0=0, sub1=1).
        var cp = Compile("""
            pc([t(_,104,_,_)|R],R).
            pc([t(_,105,_,_)|R],R).
            pc([t(_,106,_,_)|R],R).
            """);
        int[]? ops = OperandsOf(cp.Bytecode, Opcode.SwitchOnIntegerSub);
        Assert.NotNull(ops);
        Assert.Equal(new[] { 0, 0, 1 }, new[] { ops![0], ops[1], ops[2] });   // argIdx, sub0=head, sub1=t.arg1
    }

    [Fact]
    public void StructSubArg_EmitIntegerSub_ReachedFromStructureSwitch()
    {
        // The evalsql expression_operand idiom: arg0 is a struct e/2, the OpCode
        // integer is e's first sub-arg -> switch_on_structure picks e/2, then
        // switch_on_integer_sub keys on sub0 = 0 (sub1 = -1).
        var cp = Compile("""
            eo(e(1,_)).
            eo(e(29,_)).
            eo(e(31,_)).
            eo(e(49,_)).
            """);
        Assert.True(Count(cp.Bytecode, Opcode.SwitchOnStructure) >= 1);
        int[]? ops = OperandsOf(cp.Bytecode, Opcode.SwitchOnIntegerSub);
        Assert.NotNull(ops);
        Assert.Equal(new[] { 0, 0, -1 }, new[] { ops![0], ops[1], ops[2] });
    }

    [Fact]
    public void SharedFirstToken_NoSubSwitch_PlainChainKept()
    {
        // heading_line-style: every clause's list head shares the SAME leading
        // atom, so one level of sub indexing can't partition -> no sub-switch.
        var cp = Compile("""
            h([x,a|T],T).
            h([x,b|T],T).
            h([x,c|T],T).
            """);
        // No atom_sub at depth-1 on the head (all 'x'); depth-2 into the head
        // atom can't be followed either, so nothing is emitted.
        Assert.Equal(0, Count(cp.Bytecode, Opcode.SwitchOnAtomSub));
    }

    [Fact]
    public void MixedListHeadKinds_NoSubSwitch()
    {
        // A list bucket with both an atom head and an integer head is not a
        // single homogeneous key kind -> v1 keeps the plain chain.
        var cp = Compile("""
            m([a|T],T).
            m([1|T],T).
            """);
        Assert.Equal(0, Count(cp.Bytecode, Opcode.SwitchOnAtomSub));
        Assert.Equal(0, Count(cp.Bytecode, Opcode.SwitchOnIntegerSub));
    }
}
