using System.Linq;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>Phase 26 — the fused arithmetic opcodes pack their three operand
/// kinds and the op/rel into a single 32-bit word, keeping only the three
/// values as separate words: <c>a_int_bin</c> 29→17 bytes, <c>a_int_cmp</c>
/// 21→13. The disassembler still unpacks them into readable operands.</summary>
public class CompactArithEncodingTests
{
    [Fact]
    public void OpcodeSizes_AreCompact()
    {
        Assert.Equal(17, OpcodeTable.Get(Opcode.AIntBin).Size);
        Assert.Equal(13, OpcodeTable.Get(Opcode.AIntCmp).Size);
    }

    [Fact]
    public void AIntBin_DisassemblesToReadableUnpackedOperands()
    {
        // p(X, N) :- X is N * 2.  →  X0 = X1 * 2, kept as a runtime a_int_bin
        // (N is a variable, so no constant folding). Operands unpack to
        // [op=Mul, aKind=X, aVal=1(N), bKind=int, bVal=2, tKind=X, tVal=0(X)],
        // and the next instruction sits at offset 17 (the opcode is 17 bytes).
        string text = PredicateDisassembler.Disassemble("p(X, N) :- X is N * 2.")
            .First(e => e.Name == "p").Text;
        Assert.Contains("a_int_bin  [2, 3, 1, 0, 2, 3, 0]", text);
        Assert.Contains("17: proceed", text);
    }

    [Fact]
    public void AIntCmp_DisassemblesToReadableUnpackedOperands()
    {
        // q(A, B) :- A > B.  →  rel=Gt, A=X0, B=X1; next instruction at 13.
        string text = PredicateDisassembler.Disassemble("q(A, B) :- A > B.")
            .First(e => e.Name == "q").Text;
        Assert.Contains("a_int_cmp  [3, 3, 0, 3, 1]", text);
        Assert.Contains("13: proceed", text);
    }
}
