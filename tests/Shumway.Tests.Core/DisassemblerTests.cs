using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class DisassemblerTests
{
    [Fact]
    public void Iterate_OneOperandlessInstruction()
    {
        var code = new byte[] { (byte)Opcode.Proceed };
        var instrs = Disassembler.Iterate(code, 0, code.Length).ToList();
        Assert.Single(instrs);
        Assert.Equal(Opcode.Proceed, instrs[0].Op);
        Assert.Equal("proceed", instrs[0].Mnemonic);
        Assert.Empty(instrs[0].Operands);
        Assert.Equal(0, instrs[0].Address);
        Assert.Null(instrs[0].MetaSubOpcode);
    }

    [Fact]
    public void Iterate_TwoOperandInstruction_ReadsBothOperandsAtCorrectOffsets()
    {
        // get_constant 0x1234 (atom), 0x05 (reg)
        var code = new byte[9];
        code[0] = (byte)Opcode.GetConstant;
        BytecodeIO.WriteInt32(code, 1, 0x1234);
        BytecodeIO.WriteInt32(code, 5, 5);

        var d = Disassembler.Iterate(code, 0, code.Length).Single();
        Assert.Equal(Opcode.GetConstant, d.Op);
        Assert.Equal(new[] { 0x1234, 5 }, d.Operands);
    }

    [Fact]
    public void Iterate_MultipleInstructions_AdvancesByCorrectSizes()
    {
        // Layout: get_constant (9) | proceed (1) | unify_void (5) | trust_me (1) = 16 bytes
        var code = new byte[16];
        code[0] = (byte)Opcode.GetConstant;
        BytecodeIO.WriteInt32(code, 1, 100);
        BytecodeIO.WriteInt32(code, 5, 2);

        code[9] = (byte)Opcode.Proceed;

        code[10] = (byte)Opcode.UnifyVoid;
        BytecodeIO.WriteInt32(code, 11, 3);

        code[15] = (byte)Opcode.TrustMe;

        var instrs = Disassembler.Iterate(code, 0, code.Length).ToList();
        Assert.Equal(4, instrs.Count);

        Assert.Equal(0, instrs[0].Address);
        Assert.Equal(Opcode.GetConstant, instrs[0].Op);

        Assert.Equal(9, instrs[1].Address);
        Assert.Equal(Opcode.Proceed, instrs[1].Op);

        Assert.Equal(10, instrs[2].Address);
        Assert.Equal(Opcode.UnifyVoid, instrs[2].Op);
        Assert.Equal(3, instrs[2].Operands[0]);

        Assert.Equal(15, instrs[3].Address);
        Assert.Equal(Opcode.TrustMe, instrs[3].Op);
    }

    [Fact]
    public void Iterate_SwitchOnTerm_Reads4AddressOperands()
    {
        var code = new byte[17];
        code[0] = (byte)Opcode.SwitchOnTerm;
        BytecodeIO.WriteInt32(code, 1, 0x10);
        BytecodeIO.WriteInt32(code, 5, 0x20);
        BytecodeIO.WriteInt32(code, 9, 0x30);
        BytecodeIO.WriteInt32(code, 13, 0x40);

        var d = Disassembler.Iterate(code, 0, code.Length).Single();
        Assert.Equal(Opcode.SwitchOnTerm, d.Op);
        Assert.Equal(new[] { 0x10, 0x20, 0x30, 0x40 }, d.Operands);
    }

    [Fact]
    public void Iterate_MetaDbgInfo_ReadsEntryId()
    {
        var code = new byte[6];
        code[0] = (byte)Opcode.Meta;
        code[1] = (byte)MetaSubOpcode.DbgInfo;
        BytecodeIO.WriteInt32(code, 2, 0xAB);

        var d = Disassembler.Iterate(code, 0, code.Length).Single();
        Assert.Equal(Opcode.Meta, d.Op);
        Assert.Equal(MetaSubOpcode.DbgInfo, d.MetaSubOpcode);
        Assert.Equal("meta dbg_info", d.Mnemonic);
        Assert.Equal(0xAB, d.Operands[0]);
    }

    [Fact]
    public void Iterate_MetaWithUnknownSubOpcode_Throws()
    {
        var code = new byte[6];
        code[0] = (byte)Opcode.Meta;
        code[1] = 0xFF;
        var ex = Assert.Throws<InvalidOperationException>(
            () => Disassembler.Iterate(code, 0, code.Length).ToList());
        Assert.Contains("meta sub-opcode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Iterate_UnknownOpcode_Throws()
    {
        var code = new byte[] { 0xB5 };   // unassigned in the 0xA0..0xBF range
        var ex = Assert.Throws<InvalidOperationException>(
            () => Disassembler.Iterate(code, 0, code.Length).ToList());
        Assert.Contains("Unknown opcode", ex.Message);
        Assert.Contains("B5", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Iterate_TruncatedInstruction_Throws()
    {
        // get_constant claims 9 bytes; supply only 5.
        var code = new byte[5];
        code[0] = (byte)Opcode.GetConstant;
        var ex = Assert.Throws<InvalidOperationException>(
            () => Disassembler.Iterate(code, 0, code.Length).ToList());
        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Iterate_MetaMissingSubByte_Throws()
    {
        var code = new byte[1];
        code[0] = (byte)Opcode.Meta;
        Assert.Throws<InvalidOperationException>(
            () => Disassembler.Iterate(code, 0, code.Length).ToList());
    }

    [Fact]
    public void Iterate_RespectsStartAndEndOffsets()
    {
        // Bytecode: [trust_me | proceed | proceed]. Iterate over only the second proceed.
        var code = new byte[] { (byte)Opcode.TrustMe, (byte)Opcode.Proceed, (byte)Opcode.Proceed };
        var instrs = Disassembler.Iterate(code, start: 1, end: 2).ToList();
        Assert.Single(instrs);
        Assert.Equal(Opcode.Proceed, instrs[0].Op);
        Assert.Equal(1, instrs[0].Address);
    }

    [Fact]
    public void Iterate_RangeOutOfBounds_Throws()
    {
        var code = new byte[2];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Disassembler.Iterate(code, -1, 2).ToList());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Disassembler.Iterate(code, 0, 5).ToList());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Disassembler.Iterate(code, 2, 1).ToList());
    }

    [Fact]
    public void ToString_FormatsAddressMnemonicAndOperands()
    {
        var code = new byte[9];
        code[0] = (byte)Opcode.GetConstant;
        BytecodeIO.WriteInt32(code, 1, 7);
        BytecodeIO.WriteInt32(code, 5, 3);

        var d = Disassembler.Iterate(code, 0, code.Length).Single();
        string s = d.ToString();
        Assert.Contains("0x0000", s);
        Assert.Contains("get_constant", s);
        Assert.Contains("7", s);
        Assert.Contains("3", s);
    }

    [Fact]
    public void ToString_OperandlessInstruction_OmitsOperands()
    {
        var code = new byte[] { (byte)Opcode.Proceed };
        var d = Disassembler.Iterate(code, 0, code.Length).Single();
        Assert.Equal("0x0000: proceed", d.ToString());
    }
}
