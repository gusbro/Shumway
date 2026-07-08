using System.Linq;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>
/// ADR-029 — clause-epilogue peephole fusion. The Tier-0 <c>cut; deallocate_proceed</c>
/// and <c>cut; proceed</c> pairs collapse into one dispatched opcode (same total
/// width, Nop-padded), and <see cref="CompiledPredicate.BytecodeUnfused"/> reverses
/// it exactly so the Tier-1 IL describers never see a fused opcode.
/// (DeallocateExecute is deliberately NOT emitted — <c>execute</c> is a link-time
/// dispatch site the engine rewrites; fusing it would hide that swap.)
/// </summary>
public class Adr029FusionTests
{
    private static CompiledPredicate Compile(string src) =>
        new PredicateCompiler().Compile(new ClauseReader(src).ReadAll().ToList());

    [Fact]
    public void DeepCutClause_FusesToCutDeallocateProceed()
    {
        // `p(X) :- q(X), !.` — a call then a cut then the frame epilogue.
        var cp = Compile("p(X):-q(X),!.");
        string dis = PredicateDisassembler.Format("p/1", cp.Bytecode);
        Assert.Contains("cut_deallocate_proceed", dis);
        // The fused opcode is the only cut-bearing opcode; no standalone `cut`
        // or `deallocate_proceed` opcode survives in the fused stream.
        Assert.Equal(-1, FindOpcode(cp.Bytecode, Opcode.Cut));
        Assert.Equal(-1, FindOpcode(cp.Bytecode, Opcode.DeallocateProceed));
    }

    [Fact]
    public void BytecodeUnfused_ReversesTheFusion_Exactly()
    {
        var cp = Compile("p(X):-q(X),!.");
        Assert.Contains("cut_deallocate_proceed", PredicateDisassembler.Format("p/1", cp.Bytecode));

        // Un-fused: same length (Nop-padded), fused opcode replaced by cut +
        // deallocate_proceed at the same offsets — the form the IL describers read.
        Assert.Equal(cp.Bytecode.Length, cp.BytecodeUnfused.Length);
        string unf = PredicateDisassembler.Format("p/1", cp.BytecodeUnfused);
        Assert.DoesNotContain("cut_deallocate_proceed", unf);
        Assert.Contains("cut  [", unf);
        Assert.Contains("deallocate_proceed", unf);

        // At the byte where the fused opcode sits, the un-fused stream has `cut`
        // and, five bytes on, `deallocate_proceed`.
        int i = FindOpcode(cp.Bytecode, Opcode.CutDeallocateProceed);
        Assert.True(i >= 0);
        Assert.Equal((byte)Opcode.Cut, cp.BytecodeUnfused[i]);
        Assert.Equal((byte)Opcode.DeallocateProceed, cp.BytecodeUnfused[i + 5]);
    }

    [Fact]
    public void PlainClause_NoCutTerminator_IsUnchangedByFusion()
    {
        // A fact / a non-cut rule has no cut-terminated epilogue; BytecodeUnfused
        // returns the identical array (no copy).
        var cp = Compile("f(a). f(b).");
        Assert.Same(cp.Bytecode, cp.BytecodeUnfused);
    }

    private static int FindOpcode(byte[] code, Opcode target)
    {
        int pc = 0;
        while (pc < code.Length)
        {
            if ((Opcode)code[pc] == target) return pc;
            int size = OpcodeTable.Get(code[pc]).Size;
            if (size == 0) break;
            pc += size;
        }
        return -1;
    }
}
